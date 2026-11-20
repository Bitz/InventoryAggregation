# Design: Serve the last good report

## 1. Shape

```
 startup, every RefreshInterval ─┐
                                 ▼
 caller ──GET ?fresh=true──▶  Core: ReportRefresher ── one collection at a time (D24)
                                 │        │
                                 │        ▼
                                 │   InventoryReportService: collect ▸ validate ▸ aggregate (unchanged)
                                 │        │
                                 │        ▼  success only (D7)
                                 └──▶ Core: ReportStore  (latest good report + last attempt, in memory)
                                          ▲
 caller ──GET /api/inventory/categories───┘  Api: CategoryReportEndpoint reads the store
```

| Piece | Knows about | Does not know about |
|---|---|---|
| `ReportStore` | the latest good report, when it was made, the last attempt's time and outcome | how a report is built, HTTP |
| `ReportRefresher` | when to refresh, retry delays, the fresh-request floor, sharing one collection | paging, HTTP, response shape |
| `ReportRefreshService` | a hosted service that runs `ReportRefresher` on the schedule and stops it on shutdown | figures |
| `InventoryReportService` | collect ▸ validate ▸ aggregate, as before | when it runs, who waits for it |
| `CategoryReportEndpoint` | route, `fresh`, the freshness block, the one remaining failure response | how or when reports are built |

Everything under `InventoryReportService` (collector, validator, aggregator, upstream client, rate
limiter, integrity checks) is unchanged.

## 2. What a caller gets

| Situation | Response |
|---|---|
| A good report is stored | `200`, the stored report, `freshness` block, `Age` header |
| The last refresh failed, an older good report is stored | `200`, the older report, `freshness.stale: true`, `lastRefresh.outcome` names the failure kind |
| The stored report is older than `StaleAfter` | `200`, `freshness.overdue: true` |
| `fresh=true`, stored report younger than `FreshMaxAge` | `200`, the stored report, no upstream requests |
| `fresh=true`, otherwise | waits for a collection (joins one in flight or starts one); `200` with the new report, or the stored one marked stale if that collection fails |
| No good report has ever been produced | `503`, `kind: report-not-ready`, `Retry-After`, the last attempt's outcome |
| A bug | `500`, `kind: internal-error` (unchanged) |

## 3. Refresh algorithm

1. At startup `ReportRefreshService` starts a refresh at once. The host does not wait for it.
2. After a successful refresh the next one is due `RefreshInterval` (default 5 min) after the previous
   *scheduled* start, so a forced refresh does not move the schedule (D31).
3. After a failed refresh the next attempt is due after `RefreshRetryDelay` (default 30 s), doubling on
   each further failure up to `RefreshInterval`, and back to the schedule after a success (D32).
4. A refresh, scheduled or forced, that finds a collection in flight joins it instead of starting
   another (D24). At most one collection runs at a time.
5. Only a successful `ReportResult` replaces the stored report (D7). Every attempt, good or failed,
   records its time and outcome in the store.
6. A collection, once started, runs to its end or `CollectionTimeout`, whoever is waiting. Callers that
   leave stop waiting; they do not stop the collection. Shutdown cancels it (D33).

## 4. Decisions

Earlier decisions (archived with `add-inventory-category-report`, up to D26) stand unless revised below.
The rows that this change revises or relies on are restated so the change can be read on its own.

### Decisions this change revises or relies on

| # | Decision | Change | Status |
|---|---|---|---|
| D7 | No partial results. A collection that cannot prove completeness never produces figures. | Now applies to what is **stored**: a failed collection never replaces the stored report, and no partial figures are ever served. A failure no longer reaches the caller as an error while an older good report exists. | Revised 2026-11-20, owner prefers not to return errors |
| D13 | Money is `decimal`, rounded once to 2 places when the response is built. | Unchanged. | Confirmed 2026-11-20 |
| D16 | Failure status mapping. | The thirteen collection failure kinds no longer become HTTP statuses; they appear as `freshness.lastRefresh.outcome`, in logs and in metrics. The endpoint's only error statuses are `503` (`report-not-ready`), `500` (`internal-error`) and `400` for an unreadable query. | Revised 2026-11-20 |
| D17 | No response caching. | Superseded by D27: the latest good report is held and served. | Superseded 2026-11-20 |
| D18 | The response follows the expected shape and adds `summary`. | Also adds `freshness` (D34). Both additive. | Revised 2026-11-20 |
| D19 | OpenAPI document and Swagger UI in `Development` only. | Unchanged; the document now lists `200`, `400`, `500` and `503`. | Confirmed 2026-11-20 |
| D22 | One shared client-side rate limiter, 5 requests per 10 s. | Unchanged. A refresh costs 8 requests; at one refresh per 5 minutes that is under 3 % of the upstream's allowance. | Confirmed 2026-11-20 |
| D23 | No database or other persistence. | Revised by D28: a report outlives the request, in memory only. Still nothing on disk. | Revised 2026-11-20 |
| D24 | Concurrent requests share one collection in flight. | Extended: startup, scheduled and forced refreshes all share it. Cancellation changes with D33. | Revised 2026-11-20 |
| D25 | OpenTelemetry, exported over OTLP only when configured; no upstream address or key. | Unchanged; a refresh, not a request, now owns the `collect inventory` span (D35). | Confirmed 2026-11-20 |
| D26 | Each run builds an audit and logs a manifest. | Unchanged per run. `?include=audit` returns the audit of the run that produced the served report. | Confirmed 2026-11-20 |

### New decisions

| # | Decision | Why | Status |
|---|---|---|---|
| D27 | The report is built by a background refresh and served from the latest good one. A request never waits for the upstream unless it asks with `fresh=true`. | The owner prefers not to return errors to callers. A live report must either fail or show partial figures when the upstream misbehaves; a stored good report can be shown instead, honestly dated. It also removes the ~10 s wait per request and makes upstream load independent of traffic. | Confirmed 2026-11-20 |
| D28 | The stored report lives in memory only. A restart starts empty until the first refresh succeeds (~10 s when healthy). | No new dependency; the README still runs from a fresh clone. Keeping it on disk would close the gap after a restart and is the next step. | Confirmed 2026-11-20 |
| D29 | When no good report has ever been produced, the endpoint answers `503` Problem Details with `kind: report-not-ready`, `Retry-After`, and the last attempt's time and outcome. | The one case with nothing true to show. Serving figures that were never verified would break D7. The outcome tells an operator whether the upstream is down or the key is wrong. | Confirmed 2026-11-20 |
| D30 | Refresh every `RefreshInterval` (5 min). A stored report older than `StaleAfter` (1 h) is still served, with `freshness.overdue: true`. | 8 requests per 5 min is light on the upstream. Past an hour without a good refresh, callers should be told plainly, but an old good report still beats an error. | Confirmed 2026-11-20 |
| D31 | `fresh=true` asks for a live report. If the stored one is younger than `FreshMaxAge` (30 s) it is served as is; otherwise the caller waits for a collection (joining one in flight). A successful forced collection replaces the stored report for everyone; the schedule is not moved. If it fails, the caller gets the stored report marked stale, or `report-not-ready` when there is none. | Lets a caller who needs the current figures get them, without letting repeated forced calls spend the rate limit. | Confirmed 2026-11-20 |
| D32 | A failed refresh is retried after `RefreshRetryDelay` (30 s), doubling up to `RefreshInterval`, and the schedule resumes after a success. | Waiting a full interval after a failure, or at startup with nothing stored, would leave callers on `report-not-ready` or a stale report for 5 minutes. Doubling stops a dead upstream from being hit every 30 s for hours. | Proposed 2026-11-20 |
| D33 | A collection, once started, runs to its end or `CollectionTimeout`; a caller that leaves stops waiting but does not cancel it. Application shutdown cancels it. | Its result now serves every later caller, so there is no reason to throw it away. This removes the waiter counting that D24 needed. | Proposed 2026-11-20 |
| D34 | The success response adds a top-level `freshness` block (`asOf`, `ageSeconds`, `trigger`, `stale`, `overdue`, `lastRefresh`, `nextRefreshAt`) and the standard `Age` header in whole seconds. `asOf` equals `summary.generatedAt`. | Callers must be able to tell how old the figures are and whether the latest attempt failed, without reading logs. `Age` lets HTTP tooling see it too. | Proposed 2026-11-20 |
| D35 | Each refresh is its own trace: a root span `refresh inventory report` with `inventory.trigger` (`startup`, `scheduled`, `fresh`), and `collect inventory` beneath it. A request with `fresh=true` that waits on a collection links to that trace. | The work is no longer done inside a request. | Proposed 2026-11-20 |
| D36 | The new settings sit in the `InventoryApi` section with the others (§8). | One section, one options class, one validator, and the drift check already covers it. | Proposed 2026-11-20 |

## 5. Failure kinds

Kinds from collection are no longer response statuses (D16). They are reported as
`freshness.lastRefresh.outcome`, in the run manifest and on the `inventory.collections` metric.

| Kind | Raised by | Where it shows |
|---|---|---|
| `upstream-unavailable` | transport errors or transient statuses after retries | `lastRefresh.outcome` |
| `upstream-throttled` | `429`/`503` with `Retry-After` beyond the cap, or after retries | `lastRefresh.outcome` |
| `upstream-timeout` | `CollectionTimeout` exceeded | `lastRefresh.outcome` |
| `upstream-misconfigured` | `400`, `401`, `403`, `404` on an in-range request | `lastRefresh.outcome` |
| `upstream-rejected` | any other `4xx` | `lastRefresh.outcome` |
| `upstream-unexpected-status` | any status not covered above (e.g. `3xx`, `501`) | `lastRefresh.outcome` |
| `malformed-response` | `200` body that does not parse, after refetches | `lastRefresh.outcome` |
| `inconsistent-paging` | page metadata or page size disagrees, after refetches; records past the last page | `lastRefresh.outcome` |
| `incomplete-dataset` | distinct ids ≠ `totalRecords` after re-collection | `lastRefresh.outcome` |
| `conflicting-records` | same id with different content after re-collection | `lastRefresh.outcome` |
| `invalid-records` | any rejected record while `InvalidRecordPolicy = Fail` | `lastRefresh.outcome` |
| `dataset-too-large` | `totalPages` above `MaxPages` (default 1000) | `lastRefresh.outcome` |
| `internal-error` | an unhandled exception in the service itself (a bug) | `500` response; `lastRefresh.outcome` if a refresh threw |
| `report-not-ready` | no good report has been produced since startup (D29) | `503` response with `Retry-After` |

## 6. Open questions

- **D32, D33, D34, D35, D36** are proposed and not yet confirmed by the owner.
- **Keeping the report across restarts** (a JSON file or SQLite) is out of scope (D28).
- **Several instances.** Each instance refreshes on its own schedule, so upstream load grows with the
  instance count (8 requests per 5 min each, well inside the limit for a handful). A single shared
  refresher writing to a shared store is the step after that.

## 7. Observed upstream behaviour

Unchanged; see the archived change's §7 and `probe-findings.md`.

## 8. Settings

Section `InventoryApi`. Rules are enforced at startup (`service-configuration`). The last four rows are
new in this change.

| Setting | Default | Rule |
|---|---|---|
| `BaseUrl` | none | absolute `http` or `https` URI |
| `ApiKey` | none | not blank |
| `ApiKeyHeader` | `X-Api-Key` | valid header name |
| `PageSize` | 25 | 1–25 |
| `MaxConcurrency` | 4 | 1–16 |
| `MaxRetries` | 3 | 0–10 |
| `BaseDelay` | 00:00:00.200 | > 0 |
| `MaxRetryAfter` | 00:00:10 | ≥ 0 |
| `AttemptTimeout` | 00:00:10 | > 0 |
| `PageTimeout` | 00:00:30 | ≥ `AttemptTimeout` |
| `CollectionTimeout` | 00:01:00 | ≥ `PageTimeout` |
| `PageRefetchLimit` | 2 | 0–5 |
| `CollectionAttempts` | 2 | 1–3 |
| `MaxPages` | 1000 | ≥ 1 |
| `InvalidRecordPolicy` | `Report` | `Report` or `Fail` |
| `RateLimitPermits` | 5 | 1–100 |
| `RateLimitWindow` | 00:00:10 | > 0 |
| `RefreshInterval` | 00:05:00 | ≥ `CollectionTimeout` |
| `RefreshRetryDelay` | 00:00:30 | > 0 and ≤ `RefreshInterval` |
| `StaleAfter` | 01:00:00 | ≥ `RefreshInterval` |
| `FreshMaxAge` | 00:00:30 | ≥ 0 and < `RefreshInterval` |
