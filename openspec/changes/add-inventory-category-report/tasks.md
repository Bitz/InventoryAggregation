# Tasks: Add the inventory category report

Time box: the whole change is meant to fit a short build. If a section runs over, stop, and write what
was left and how it would be done into the README's limitations section instead of rushing it.

## 0. Probe the upstream first
- [ ] Get the base URL and API key; store the key with `dotnet user-secrets` (never in a file in the repo).
- [ ] Requests use `C:\Windows\System32\curl.exe` with `-A ""` (no User-Agent), only the API key header
      added, paced rather than burst.
- [ ] Throwaway script (outside the repo, or under the git-ignored `recordings/`): fetch every page at
      page size 25 a few times, plus page `totalPages + 1`, page 0 and page size 26. Save the raw
      responses with status, headers and timing.
- [ ] Diff the runs. Record every behaviour seen (status codes, `Retry-After`, duplicates, drift,
      short pages, bad records, latency) in `design.md` §7, and close the questions in §6.
- [ ] Revisit the defaults in `service-configuration` against what was seen.

## 1. Scaffold
- [ ] `global.json` pinned to the .NET 10 SDK band, `rollForward: latestFeature`.
- [ ] Solution with `src/InventoryAggregation.Core`, `src/InventoryAggregation.Api`,
      `tests/InventoryAggregation.Core.Tests`, `tests/InventoryAggregation.Api.Tests`.
- [ ] `Directory.Build.props`: nullable on, warnings as errors, implicit usings.
- [ ] Confirm Core has no reference to ASP.NET Core or `System.Net.Http` types in its API.

## 2. Configuration (`service-configuration`)
- [ ] `InventoryApiOptions` with validation and `ValidateOnStart`; startup failure names the setting.
- [ ] `appsettings.json` with defaults and no key; `UserSecretsId` on the Api project.
- [ ] `AddInventoryAggregation(IConfiguration)` registers every own component and `TimeProvider.System`.
- [ ] Header redaction for the key in `IHttpClientFactory` logging.

## 3. Upstream client (`inventory-source`)
- [ ] Envelope and raw-record DTOs (nullable fields, `decimal` price).
- [ ] `InventoryApiClient : IInventoryPageSource` as a typed client.
- [ ] Resilience handler: total timeout ▸ retry (transient predicate, backoff with jitter,
      `Retry-After` delay with cap) ▸ attempt timeout. Wire the pipeline's `TimeProvider` for tests.
- [ ] Status classification and `PageResult` / failure kinds; cancellation passes through.
- [ ] Retry log entries.

## 4. Collection (`inventory-collection`)
- [ ] Page 1 reference, bounded-concurrency fetch of the rest.
- [ ] Per-page checks and refetch.
- [ ] End-of-data check.
- [ ] Reconciliation, one re-collection, failure details.
- [ ] `MaxPages`, `CollectionTimeout`, caller cancellation.
- [ ] Outcome counters.

## 5. Records and aggregation (`inventory-records`, `category-aggregation`)
- [ ] `RecordValidator`: all reasons per record, normalization, policy.
- [ ] `CategoryAggregator`: zero-quantity drop, grouping and display name, figures, order.
- [ ] `InventoryReportService`: collect ▸ validate ▸ aggregate, returns report or failure.

## 6. Endpoint (`category-report-endpoint`)
- [ ] `GET /api/inventory/categories` and response contract.
- [ ] One failure-kind ▸ status map; Problem Details with `kind` and `traceId`; `Retry-After` on `503`.
- [ ] Global exception handler; no stack traces outside Development.
- [ ] `RequestAborted` passed through; cancellation not logged as an error.

## 7. Tests
- [ ] Core: validator rules and normalization; aggregator figures, rounding, ties, ordering, spelling
      variants, all-zero category; collector against a scripted page source covering every scenario in
      `inventory-collection` (wrong page, drift, short page, overlap, conflicts, past-the-end records,
      one-off glitch, too large, timeout, cancellation, concurrency bound).
- [ ] Api: client over a scripted `HttpMessageHandler` and `FakeTimeProvider` covering every scenario in
      `inventory-source` (retry then success, exhaustion, `Retry-After` honoured and too long, fail-fast
      statuses, unknown status, hang, truncated body, cancellation, key not in URL or logs).
- [ ] Api: endpoint tests with `WebApplicationFactory` and a fake page source, one per failure kind, plus
      the healthy contract and the summary arithmetic.
- [ ] No test sleeps on real time.

## 8. Local fault stub
- [ ] `tools/FakeInventoryApi`: a minimal app that serves a generated static dataset on
      `/api/records` and takes a fault mode per run (`healthy`, `flaky-500`, `throttle`, `hang`,
      `drift-totals`, `overlap`, `short-page`, `wrong-page`, `conflict`, `bad-record`, `truncated`,
      `extra-page`, `unknown-status`). Lets anyone reproduce a reported upstream behaviour in minutes
      and see which failure kind the service names.
- [ ] README section on running the service against the stub.

## 9. Documentation
- [ ] README: prerequisites, configuring the key (user secrets and environment variable), running the
      app, running the tests, the endpoint with a sample `curl` and sample responses (success and one
      failure).
- [ ] Architecture note, half a page: the pieces in `design.md` §1 and why the splits are where they are.
- [ ] Assumptions and decisions: D1–D18 in plain words, and every response-shape deviation.
- [ ] Known limitations and next steps: no caching or request coalescing (D17), single-instance only,
      no metrics export, anything from §0–§8 left undone, and what would change with more time.

## 10. Verify
- [ ] Fresh clone into an empty folder, follow the README word for word, on a machine state with only
      the .NET 10 SDK: build, test, run, call the endpoint.
- [ ] Search the full history for the key value.
- [ ] Every scenario in the six specs maps to a test or to a stated limitation.
- [ ] Rehearse a change: add one more per-category figure end to end and note which files it touched.
      If more than the aggregator, its result type, the response mapping and tests changed, simplify.
