# Tasks: Add the inventory category report

Time box: the whole change is meant to fit a short build. If a section runs over, stop, and write what
was left and how it would be done into the README's limitations section instead of rushing it.

## 0. Probe the upstream first
- [x] Base URL in user secrets (id `inventory-aggregation-7d3f2c1e`, reused as the Api project's
      `UserSecretsId`), so it stays out of the repo too.
- [x] API key in the same store: `dotnet user-secrets set "InventoryApi:ApiKey" "<key>" --id inventory-aggregation-7d3f2c1e`.
- [x] Requests use `C:\Windows\System32\curl.exe` with `-A ""` (no User-Agent), only the API key header
      added, paced rather than burst.
- [x] `tools/probe/Invoke-UpstreamProbe.ps1`: every page at size 25 three times, one pass each at 20
      and 7, past-the-end, and out-of-range edges. Raw responses go to the git-ignored `recordings/`.
      Headers checked against a local echo listener first.
- [x] `tools/probe/Get-ProbeSummary.ps1`: statuses, latency, paging checks, cross-pass differences,
      conflicting ids, record quality, category spellings.
- [x] Run both. Record every behaviour seen in `probe-findings.md`, its short form in `design.md` §7,
      and close the questions in §6 (2026-11-20).
- [x] Spec changes the data forced: page overlap (D21), rate-limit pacing (D22).
- [x] Revisit the defaults in `service-configuration` against what was seen: `RateLimitPermits` and
      `RateLimitWindow` added; `MaxRetryAfter` 10 s covers the 6 s maximum seen; the rest unchanged.

## 1. Scaffold
- [x] `global.json` pinned to the .NET 10 SDK band, `rollForward: latestFeature`.
- [x] Solution with `src/InventoryAggregation.Core`, `src/InventoryAggregation.Api`,
      `tests/InventoryAggregation.Core.Tests`, `tests/InventoryAggregation.Api.Tests`.
- [x] `Directory.Build.props`: nullable on, warnings as errors, implicit usings.
- [x] Confirm Core has no reference to ASP.NET Core or `System.Net.Http` types in its API.

## 2. Configuration (`service-configuration`)
- [x] `InventoryApiOptions` with validation and `ValidateOnStart`; startup failure names the setting.
- [x] `appsettings.json` with defaults and no key; `UserSecretsId` on the Api project.
- [x] `AddInventoryAggregation(IConfiguration)` registers every own component and `TimeProvider.System`.
- [x] Header redaction for the key in `IHttpClientFactory` logging.

## 3. Upstream client (`inventory-source`)
- [x] Envelope and raw-record DTOs (nullable fields, `decimal` price).
- [x] `InventoryApiClient : IInventoryPageSource` as a typed client.
- [x] Resilience handler: total timeout ▸ retry (transient predicate, backoff with jitter,
      `Retry-After` delay with cap) ▸ attempt timeout. Wire the pipeline's `TimeProvider` for tests.
- [x] Status classification and `PageResult` / failure kinds; cancellation passes through.
- [x] Shared rate limiter (`System.Threading.RateLimiting`, 5 per 10 s) registered as a singleton and placed so
      retries also take a permit (D22). Each permit returns a full window after its request finished, rather
      than at a client-side fixed-window boundary: the upstream's window boundaries are not visible to us, and a
      client fixed window that straddled one could let up to 10 requests into a single upstream window.
- [x] Retry log entries.

## 4. Collection (`inventory-collection`)
- [x] Page 1 reference, bounded-concurrency fetch of the rest.
- [x] Per-page checks and refetch.
- [x] End-of-data check.
- [x] Reconciliation, one re-collection, failure details.
- [x] `MaxPages`, `CollectionTimeout`, caller cancellation.
- [x] Outcome counters.

## 5. Records and aggregation (`inventory-records`, `category-aggregation`)
- [x] `RecordValidator`: all reasons per record, normalization, policy.
- [x] `CategoryAggregator`: zero-quantity drop, grouping and display name, figures, order.
- [x] `InventoryReportService`: collect ▸ validate ▸ aggregate, returns report or failure.
- [x] Concurrent callers share the collection in flight (D24); the shared run stops only when every waiting caller has gone.

## 6. Endpoint (`category-report-endpoint`)
- [x] `GET /api/inventory/categories` and response contract.
- [x] One failure-kind ▸ status map; Problem Details with `kind` and `traceId`; `Retry-After` on `503`.
- [x] Global exception handler; no stack traces outside Development.
- [x] `RequestAborted` passed through; cancellation not logged as an error.
- [x] OpenAPI document at `/openapi/v1.json` in Development only (D19), describing the success and Problem Details responses.

## 7. Tests
- [x] Core: validator rules and normalization; aggregator figures, rounding, ties, ordering, spelling
      variants, all-zero category; collector against a scripted page source covering every scenario in
      `inventory-collection` (wrong page, drift, short page, overlapping page, overlap with omission,
      conflicts, past-the-end records,
      one-off glitch, too large, timeout, cancellation, concurrency bound).
- [x] Api: client over a scripted `HttpMessageHandler` and `FakeTimeProvider` covering every scenario in
      `inventory-source` (retry then success, exhaustion, `Retry-After` honoured and too long, fail-fast
      statuses, unknown status, hang, truncated body, cancellation, key not in URL or logs, pacing at
      5 per 10 s across two concurrent collections).
- [x] Api: endpoint tests with `WebApplicationFactory` and a fake page source, one per failure kind, plus
      the healthy contract and the summary arithmetic.
- [x] No test sleeps on real time. Retries, backoff, timeouts and pacing run on `FakeTimeProvider`; the clock driver
      pauses about a millisecond between fake-clock steps so thread-pool continuations run before time moves on.

## 8. Local fault stub
- [x] `tools/FakeInventoryApi`: a minimal app that serves a generated static dataset on
      `/api/records` and takes a fault mode per run (`healthy` (as observed: even-page overlap, padded mixed-case
      categories, 5-per-10-s limit), `flaky-500`, `throttle`, `hang`,
      `drift-totals`, `overlap`, `short-page`, `wrong-page`, `conflict`, `bad-record`, `truncated`,
      `extra-page`, `unknown-status`). Lets anyone reproduce a reported upstream behaviour in minutes
      and see which failure kind the service names.
- [ ] README section on running the service against the stub.

## 9. Documentation
- [ ] README: prerequisites, configuring the key (user secrets and environment variable), running the
      app, running the tests, the endpoint with a sample `curl` and sample responses (success and one
      failure).
- [ ] Architecture note, half a page: the pieces in `design.md` §1 and why the splits are where they are.
- [ ] Assumptions and decisions: D1–D24 in plain words, and every response-shape deviation.
- [ ] Known limitations and next steps: no caching (D17); coalescing (D24) works within one instance
      only, so several instances would each collect and share the upstream's rate limit; no persistence
      (D23); no metrics export, anything from §0–§8 left undone, and what would change with more time.

## 10. Verify
- [ ] Fresh clone into an empty folder, follow the README word for word, on a machine state with only
      the .NET 10 SDK: build, test, run, call the endpoint.
- [ ] Search the full history for the key value.
- [ ] Every scenario in the six specs maps to a test or to a stated limitation.
- [ ] Rehearse a change: add one more per-category figure end to end and note which files it touched.
      If more than the aggregator, its result type, the response mapping and tests changed, simplify.
