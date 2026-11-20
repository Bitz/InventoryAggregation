# Design: Inventory category report

## 1. Shape

```
 caller ──GET /api/inventory/categories──▶ Api: CategoryReportEndpoint
                                              │
                                              ▼
                               Core: InventoryReportService
                                 │            │             │
                                 ▼            ▼             ▼
                      InventoryCollector  RecordValidator  CategoryAggregator
                                 │         (pure)           (pure)
                                 ▼
                     IInventoryPageSource  (Core abstraction: page in, page result out)
                                 ▲
                                 │ implements
                 Api/Upstream: InventoryApiClient (typed HttpClient)
                                 │
                  resilience handler: total timeout ▸ retry ▸ attempt timeout
                                 │
                                 ▼
                         Inventory API  GET /api/records
```

| Piece | Knows about | Does not know about |
|---|---|---|
| `InventoryApiClient` | HTTP, status codes, JSON envelope, API key header | paging strategy, totals, categories |
| `InventoryCollector` | pages, totals, refetching, reconciliation | HTTP, status codes, JSON |
| `RecordValidator` | field rules, normalization | where records came from |
| `CategoryAggregator` | zero-quantity rule, grouping, figures | validation, paging, HTTP |
| `InventoryReportService` | orchestration: collect ▸ validate ▸ aggregate | HTTP |
| `CategoryReportEndpoint` | route, response contract, failure ▸ status mapping | how figures are computed |

## 2. Two layers of resilience

The HTTP layer and the dataset layer catch different faults, and neither can stand in for the other.

1. **HTTP layer** (`Microsoft.Extensions.Http.Resilience` on the typed client). It sees status codes,
   transport exceptions and time. It retries transient failures with exponential backoff and jitter,
   honours `Retry-After`, and bounds each attempt and the whole page fetch. What the library does for
   us: the retry loop, backoff with decorrelated jitter, `Retry-After` parsing hook, per-attempt and
   total timeouts that cancel the underlying request, and logging/metrics hooks. We supply the
   predicate (what counts as transient) and the delay rule for `Retry-After`.
2. **Dataset layer** (`InventoryCollector`). It sees a `200` whose content is wrong: a page that does not
   match the page asked for, totals that drift between pages, short pages, duplicate or conflicting
   ids, a count that does not reconcile. The HTTP pipeline cannot see any of these. Because the
   dataset is static, the collector settles a disagreement by asking again, then gives up with a
   named reason.

## 3. Collection algorithm

1. Fetch page 1 with `PageSize` (default 25). Its `totalPages` and `totalRecords` become the reference.
   Check `totalPages == ceil(totalRecords / PageSize)` (with 0 or 1 accepted when `totalRecords` is 0).
2. Fetch pages 2..`totalPages`, at most `MaxConcurrency` in flight (default 4).
3. Check each page against the reference (§ inventory-collection). A page may hold more records than
   the page size (D21); only fewer is a fault. A page that fails a check, or whose body could not be
   parsed, is fetched again, up to `PageRefetchLimit` (default 2) extra times.
4. Fetch page `totalPages + 1`. An empty `records` array or a `400` confirms the end. Records there mean
   the totals understate the data.
5. Reconcile: collapse identical duplicates by `id`, flag conflicting ones, and require the count of
   distinct ids to equal `totalRecords`.
6. If reconciliation fails, run steps 1–5 once more from scratch (`CollectionAttempts` default 2). If it
   still fails, return the failure with expected count, collected count, duplicate ids and conflicting
   ids.
7. Everything above runs under `CollectionTimeout` (default 60 s) and the caller's cancellation token.
8. Every request first waits for a permit from the shared rate limiter (D22). Under the upstream's limit
   of 5 per 10 s, a healthy 157-record collection is 8 requests and takes just over 10 s.

## 4. Decisions

"Confirmed" means the owner agreed it. "Probe supports" means the recorded upstream data
(`probe-findings.md`) agrees with the rule; the owner has not re-confirmed it since. "Forced by probe"
means the data made the change necessary. "Proposed" has not been put to the owner.

| # | Decision | Why | Status |
|---|---|---|---|
| D1 | Two source projects: `Core` (no HTTP) and `Api` (host plus upstream client). Collection logic lives in Core behind `IInventoryPageSource`. | The compiler keeps HTTP out of the business logic. Paging and integrity checks are business rules about the data and need testing without a network. A third Infrastructure project adds ceremony for one class. | Confirmed 2026-11-20 |
| D2 | Resilience split across the HTTP layer and the dataset layer (§2). | A `200` with wrong content passes every HTTP-level policy. | Confirmed 2026-11-20 (resilience library) |
| D3 | `PageSize` defaults to 25, the documented maximum. | Fewest requests, so fewest chances to fail. Configurable 1–25. | Confirmed 2026-11-20 |
| D4 | Page 1 alone, then the rest with bounded concurrency (`MaxConcurrency` 4, 1 allowed). | Page 1 gives the totals. Under the observed rate limit, concurrency only fills the first window faster; pacing (D22) sets the total time. | Probe supports, 2026-11-20 |
| D5 | `Retry-After` is honoured up to `MaxRetryAfter` (default 10 s). Longer than that, the page fails as throttled without waiting. | A caller should not hang for a minute; retrying sooner than asked is pointless. The endpoint passes the upstream's `Retry-After` on. | Confirmed 2026-11-20 |
| D6 | A `200` whose body does not parse is not retried by the HTTP layer; the collector refetches it as a page fault. | The HTTP handler has finished before deserialization runs. Treating it as a page fault keeps one refetch rule. | Confirmed 2026-11-20 |
| D7 | No partial results. If completeness cannot be proven, the endpoint returns an error, never figures. | Figures from part of the data look valid and are wrong. | Confirmed 2026-11-20 |
| D8 | End of data is confirmed with one request past `totalPages`; an empty page or a `400` both count as the end. | Catches understated totals. Which of the two the upstream returns is unconfirmed (§6). | Confirmed 2026-11-20 |
| D9 | Duplicate ids with identical content collapse to one. Duplicate ids with different content trigger a refetch; if the conflict survives, collection fails. | The service will not guess which version of a record is true. Observed: every repeat is byte-identical. | Probe supports, 2026-11-20 |
| D10 | Invalid records follow `InvalidRecordPolicy`: `Report` (default) excludes them from every figure and lists them with reasons in the response; `Fail` refuses the whole report. | One malformed row should not take the report down, but it must not vanish silently either. The strict mode exists for consumers who need it. | Confirmed 2026-11-20 |
| D11 | Categories group on the trimmed name, case-insensitively (ordinal). The displayed name is the most frequent trimmed spelling, ties broken by ordinal order. | `"Electronics"` and `"electronics "` are one category to a reader. The rule is deterministic. Observed: 6 spellings per category; the most frequent trimmed one is the conventional spelling in each (`Office Supplies`, `Toys`, ...). | Probe supports, 2026-11-20 |
| D12 | Zero-quantity records are dropped before grouping. A category whose records are all zero-quantity does not appear. The count of dropped records is in `summary`. Negative quantity is invalid (D10), not zero. | "Exclude from every figure" leaves nothing to report for such a category. Negative stock is a data error, not empty stock. Observed: a `Discontinued` category whose 7 records are all quantity 0, so it drops out; no negative quantities. | Probe supports, 2026-11-20 |
| D13 | Money is `decimal` throughout. `price × quantity` is summed unrounded and rounded once, to 2 places, `MidpointRounding.AwayFromZero`, when the response is built. Prices with more than 2 places are accepted. | No float drift; no accumulated rounding error. | Confirmed 2026-11-20 |
| D14 | `lastUpdated` is compared as a UTC instant. A value with an offset (`Z` or `±hh:mm`) is converted to UTC; a value without one is read as UTC and counted in `summary.assumedUtcTimestamps`. Most recently updated = latest instant; ties go to the lowest `id`. | The contract says the field is UTC, so reading an offset-less value as UTC follows it; rejecting would drop otherwise good records. The count keeps the assumption visible. Observed: every timestamp ends in `Z` (2026-11-20). Deterministic tie-break. | Confirmed 2026-11-20 |
| D15 | Categories are ordered by display name (ordinal, case-insensitive); items within a category by `id`. | Stable output, diffable between runs. | Confirmed 2026-11-20 |
| D16 | Failure status mapping: upstream throttling ▸ `503` with `Retry-After`; deadline exceeded ▸ `504`; upstream `400`/`401`/`403`/`404` on an in-range request ▸ `500` (our configuration is wrong); every other upstream or integrity failure ▸ `502`. All as RFC 9457 Problem Details with a failure `kind` and `traceId`. | The status tells the caller whose fault it is and whether retrying helps. | Confirmed 2026-11-20 |
| D17 | No response caching in this change. Each report is collected fresh. | The data is static, so caching is tempting, but it would hide upstream changes and needs an invalidation story. Concurrent callers share one collection instead (D24). | Confirmed 2026-11-20 |
| D18 | Response follows the expected shape and adds a `summary` block (counts, pages, rejected records). | Additive, so a client of the expected shape is unaffected; the summary shows the integrity checks happened. | Confirmed 2026-11-20 |
| D19 | The built-in ASP.NET Core OpenAPI document (`Microsoft.AspNetCore.OpenApi`) is served at `/openapi/v1.json` in `Development` only, with Swagger UI (`Swashbuckle.AspNetCore.SwaggerUI`, the UI package alone) at `/swagger` reading that document. Neither is served outside `Development`. | Callers get a machine-readable contract, including the Problem Details responses, and developers can try the endpoint in a browser. The UI renders the built-in document rather than generating its own, so there is one source for the contract, and nothing is exposed in production. | Changed 2026-11-20 at the owner's request: Swagger UI added (was: no UI package) |
| D20 | When reconciliation fails, the whole collection runs once more from scratch before failing (`CollectionAttempts` 2, §3 step 6). | The dataset is static, so a second pass separates a one-off glitch from a real fault in the data. | Confirmed 2026-11-20 |
| D21 | A page may hold more records than the page size. Only a page with fewer records than expected is a page fault. Extra records are judged at reconciliation: identical repeats collapse (D9); anything else fails there. | Observed: every even-numbered page repeats the last record of the page before it, at page sizes 25, 20 and 7, on every pass. A strict count would fail every report, and refetching returns the same page. | Forced by probe, 2026-11-20 |
| D22 | One client-side rate limiter, shared by the whole app, paces upstream requests to `RateLimitPermits` per `RateLimitWindow` (5 per 10 s), retries included. Requests wait for a permit instead of being sent and retried. | Observed: the upstream allows 5 requests per 10 s and answers `429` with an accurate `Retry-After` beyond that. A collection is 8 requests, so without pacing every report would hit `429`. Pacing keeps `429` an exception path. | Forced by probe, 2026-11-20 |
| D23 | No database or other persistence. Nothing the service computes outlives the request that asked for it. | Every report is derived from the upstream on demand and the service writes nothing of its own. A store would act as a long-lived cache (D17), and adds a server, a connection string and migrations to setup. Revisit for history between runs, or for serving a dated last-good report while the upstream is down. | Confirmed 2026-11-20 |
| D24 | Concurrent report requests share one collection in flight. A request arriving while a collection runs waits for that collection's outcome; one arriving after it finishes starts a new one. Nothing is kept once it ends. | Each collection needs 8 requests against 5 permits per 10 s (D22), so a second caller would otherwise wait a full window behind the first. Sharing halves that and, unlike a cache, can never serve data older than the request. | Confirmed 2026-11-20 |
| D25 | Logs, traces and metrics go through OpenTelemetry and are exported over OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, using the standard `OTEL_*` settings. Upstream request spans keep the path and query but lose the host, port and full URL; the HTTP client's own metrics are not collected because they are tagged with the upstream host. Our meter `InventoryAggregation` counts collections by outcome, retries, refetches and re-collections. | Errors and retries must be visible after the fact without tying the service to one vendor: any OTLP backend (a collector, Grafana, Datadog, Application Insights, Sentry) can receive them. Off by default, so nothing leaves the machine unless someone points it somewhere. The upstream address is treated like a secret, as in the logs. | Confirmed 2026-11-20 (owner asked for OpenTelemetry with OTLP) |

## 5. Failure kinds

| Kind | Raised by | Status (D16) |
|---|---|---|
| `upstream-unavailable` | transport errors or transient statuses after retries | 502 |
| `upstream-throttled` | `429`/`503` with `Retry-After` beyond the cap, or after retries | 503 |
| `upstream-timeout` | `CollectionTimeout` exceeded | 504 |
| `upstream-misconfigured` | `400`, `401`, `403`, `404` on an in-range request | 500 |
| `upstream-rejected` | any other `4xx` | 502 |
| `upstream-unexpected-status` | any status not covered above (e.g. `3xx`, `501`) | 502 |
| `malformed-response` | `200` body that does not parse, after refetches | 502 |
| `inconsistent-paging` | page metadata or page size disagrees, after refetches; records past the last page | 502 |
| `incomplete-dataset` | distinct ids ≠ `totalRecords` after re-collection | 502 |
| `conflicting-records` | same id with different content after re-collection | 502 |
| `invalid-records` | any rejected record while `InvalidRecordPolicy = Fail` | 502 |
| `dataset-too-large` | `totalPages` above `MaxPages` (default 1000) | 502 |
| `internal-error` | an unhandled exception in the service itself (a bug) | 500 |

## 6. Open questions

- ~~**API key header name.**~~ Confirmed `X-Api-Key` (2026-11-20): a request without it gets `401`
  with Problem Details naming the header. `ApiKeyHeader` stays configurable.
- ~~**Past-the-end behaviour.**~~ Empty `200` echoing the requested `page`, at every page size and far
  past the end (2026-11-20). The `400` branch of D8 stays as a safety net.
- ~~**`Retry-After`.**~~ Sent on `429` in whole seconds, counting down to the end of the current 10 s
  window (2026-11-20). `503` not seen.
- ~~**Observed faults.**~~ Recorded in §7 and `probe-findings.md` (2026-11-20).
- ~~**Concurrent reports.**~~ Concurrent callers share one collection in flight, nothing cached after it
  (D24, 2026-11-20).

## 7. Observed upstream behaviour

Short form of `probe-findings.md`. One row per behaviour seen, with the date and how the design
handles it.

| Seen | Behaviour | Handled by |
|---|---|---|
| 2026-11-20 | No key: `401`, Problem Details body, `detail` names the `X-Api-Key` header. No `WWW-Authenticate`. | `upstream-misconfigured` (D16) |
| 2026-11-20 | Wrong key: the identical `401`. A missing key and a rejected key look the same. | `upstream-misconfigured`; message says "missing or rejected" |
| 2026-11-20 | Rate limit of 5 requests per 10 s, fixed window. Beyond it, `429` with `Retry-After` in whole seconds (1–6 seen) counting down to the window's end. | Pacing (D22); `Retry-After` (D5) as fallback |
| 2026-11-20 | Every even-numbered page repeats the previous page's last record as its first, byte for byte, at page sizes 25, 20 and 7, on every pass. Nothing is ever missing. | D21, D9 |
| 2026-11-20 | 157 records, ids 1001–1157, identical across 5 passes and 3 page sizes. Totals constant on every page. | Reconciliation passes |
| 2026-11-20 | Category values padded with spaces and in mixed case, 6 spellings per category. | D11 |
| 2026-11-20 | `Discontinued` category with 7 records, all quantity 0. 21 zero-quantity records in all. | D12: the category drops out |
| 2026-11-20 | Past the end: empty `200`. Out of range (`page` < 1 or non-numeric, `pageSize` 0 or 26): `400` Problem Details naming the parameter. | D8; `upstream-misconfigured` |
| 2026-11-20 | Not seen in 125 requests: `500`, timeouts, malformed bodies, nulls, negatives, non-`Z` offsets, prices beyond 2 places, `lastUpdated` ties. | Specified anyway; tests use synthetic faults |

## 8. Settings

Section `InventoryApi`. Rules are enforced at startup (`service-configuration`).

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
