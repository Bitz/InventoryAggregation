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
3. Check each page against the reference (§ inventory-collection). A page that fails a check, or whose
   body could not be parsed, is fetched again, up to `PageRefetchLimit` (default 2) extra times.
4. Fetch page `totalPages + 1`. An empty `records` array or a `400` confirms the end. Records there mean
   the totals understate the data.
5. Reconcile: collapse identical duplicates by `id`, flag conflicting ones, and require the count of
   distinct ids to equal `totalRecords`.
6. If reconciliation fails, run steps 1–5 once more from scratch (`CollectionAttempts` default 2). If it
   still fails, return the failure with expected count, collected count, duplicate ids and conflicting
   ids.
7. Everything above runs under `CollectionTimeout` (default 60 s) and the caller's cancellation token.

## 4. Decisions

| # | Decision | Why |
|---|---|---|
| D1 | Two source projects: `Core` (no HTTP) and `Api` (host plus upstream client). Collection logic lives in Core behind `IInventoryPageSource`. | The compiler keeps HTTP out of the business logic. Paging and integrity checks are business rules about the data and need testing without a network. A third Infrastructure project adds ceremony for one class. |
| D2 | Resilience split across the HTTP layer and the dataset layer (§2). | A `200` with wrong content passes every HTTP-level policy. |
| D3 | `PageSize` defaults to 25, the documented maximum. | Fewest requests, so fewest chances to fail. Configurable 1–25. |
| D4 | Page 1 alone, then the rest with bounded concurrency (`MaxConcurrency` 4, 1 allowed). | Page 1 gives the totals. Bounded parallelism cuts latency without hammering an upstream that may throttle. |
| D5 | `Retry-After` is honoured up to `MaxRetryAfter` (default 10 s). Longer than that, the page fails as throttled without waiting. | A caller should not hang for a minute; retrying sooner than asked is pointless. The endpoint passes the upstream's `Retry-After` on. |
| D6 | A `200` whose body does not parse is not retried by the HTTP layer; the collector refetches it as a page fault. | The HTTP handler has finished before deserialization runs. Treating it as a page fault keeps one refetch rule. |
| D7 | No partial results. If completeness cannot be proven, the endpoint returns an error, never figures. | Figures from part of the data look valid and are wrong. |
| D8 | End of data is confirmed with one request past `totalPages`; an empty page or a `400` both count as the end. | Catches understated totals. Which of the two the upstream returns is unconfirmed (§6). |
| D9 | Duplicate ids with identical content collapse to one. Duplicate ids with different content trigger a refetch; if the conflict survives, collection fails. | The service will not guess which version of a record is true. |
| D10 | Invalid records follow `InvalidRecordPolicy`: `Report` (default) excludes them from every figure and lists them with reasons in the response; `Fail` refuses the whole report. | One malformed row should not take the report down, but it must not vanish silently either. The strict mode exists for consumers who need it. |
| D11 | Categories group on the trimmed name, case-insensitively (ordinal). The displayed name is the most frequent trimmed spelling, ties broken by ordinal order. | `"Electronics"` and `"electronics "` are one category to a reader. The rule is deterministic. |
| D12 | Zero-quantity records are dropped before grouping. A category whose records are all zero-quantity does not appear. The count of dropped records is in `summary`. Negative quantity is invalid (D10), not zero. | "Exclude from every figure" leaves nothing to report for such a category. Negative stock is a data error, not empty stock. |
| D13 | Money is `decimal` throughout. `price × quantity` is summed unrounded and rounded once, to 2 places, `MidpointRounding.AwayFromZero`, when the response is built. Prices with more than 2 places are accepted. | No float drift; no accumulated rounding error. |
| D14 | `lastUpdated` must carry an offset (`Z` or `±hh:mm`) and is compared as a UTC instant. No offset makes the record invalid. Most recently updated = latest instant; ties go to the lowest `id`. | The contract says UTC; guessing a zone is a silent error. Deterministic tie-break. |
| D15 | Categories are ordered by display name (ordinal, case-insensitive); items within a category by `id`. | Stable output, diffable between runs. |
| D16 | Failure status mapping: upstream throttling ▸ `503` with `Retry-After`; deadline exceeded ▸ `504`; upstream `400`/`401`/`403`/`404` on an in-range request ▸ `500` (our configuration is wrong); every other upstream or integrity failure ▸ `502`. All as RFC 9457 Problem Details with a failure `kind` and `traceId`. | The status tells the caller whose fault it is and whether retrying helps. |
| D17 | No response caching in this change. Each request collects fresh. | The data is static, so caching is tempting, but it would hide upstream changes and needs an invalidation story. Listed as a next step with request coalescing. |
| D18 | Response follows the expected shape and adds a `summary` block (counts, pages, rejected records). | Additive, so a client of the expected shape is unaffected; the summary shows the integrity checks happened. |

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

## 6. Open questions

- **API key header name.** Not confirmed until the connection details arrive. `ApiKeyHeader` is
  configurable, default `X-Api-Key`. Confirm, and check whether a query-string key is also accepted
  (we will not use it either way).
- **Past-the-end behaviour.** Does `page = totalPages + 1` return an empty `200` or a `400`? D8 accepts
  both; record which.
- **`Retry-After`.** Does the upstream send it on `429`/`503`, and in seconds or HTTP-date form?
- **Observed faults.** Run the probe (tasks §0) and fill §7 before tuning the defaults.

## 7. Observed upstream behaviour

Filled in from the probe run. One row per behaviour seen, with the date and how the design handles it.

| Seen | Behaviour | Handled by |
|---|---|---|
| | | |

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
