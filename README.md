# Inventory Aggregation Service

An inventory reporting service. It reads every inventory record from the upstream Inventory API, checks
that what it collected is complete and consistent, groups the records by product category, and serves
the figures from one HTTP endpoint: what is on hand, what it is worth, and what changed last.

A figure computed from a partial or inconsistent dataset is worse than no figure, so the service refuses
to answer rather than answer wrongly, and says why.

## Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | 10.0 | building, running and testing the service |
| Node.js | 20 or later | spec validation (`npm run spec:check`) only |
| PowerShell | 7.2 or later | the upstream probe in `tools/probe` only |

## Configure the upstream

The service needs the upstream's base URL and an API key. Neither is stored in the repository.

**Locally: .NET user secrets.** The Api project's `UserSecretsId` is `inventory-aggregation-7d3f2c1e`:

```bash
dotnet user-secrets set "InventoryApi:BaseUrl" "<base URL>" --id inventory-aggregation-7d3f2c1e
dotnet user-secrets set "InventoryApi:ApiKey" "<API key>" --id inventory-aggregation-7d3f2c1e
```

Check what is set without printing values:

```bash
dotnet user-secrets list --id inventory-aggregation-7d3f2c1e
```

User secrets are read in the `Development` environment only, which is what the launch profile uses.

**Elsewhere: environment variables.** These take precedence over user secrets:

```bash
export InventoryApi__BaseUrl="<base URL>"
export InventoryApi__ApiKey="<API key>"
```

The key is sent in the `X-Api-Key` header and nowhere else. Every other setting has a default in
`src/InventoryAggregation.Api/appsettings.json`; the full list with ranges is in
[design.md §8](openspec/changes/add-inventory-category-report/design.md). A missing or out-of-range setting
stops the service at startup with a message that names it, for example:

```
InventoryApi:PageSize is 50; it must be from 1 to 25.
```

## Run

```bash
dotnet run --project src/InventoryAggregation.Api
```

The service listens on `http://localhost:5080`. In `Development`, Swagger UI is at
`http://localhost:5080/swagger` and the OpenAPI document it renders is at `http://localhost:5080/openapi/v1.json`.
Neither is served in other environments.

## Call the endpoint

```bash
curl -s http://localhost:5080/api/inventory/categories
```

A report takes a little over 10 seconds against the real upstream, because it allows 5 requests per
10 seconds and a full read needs 8 (see [Upstream behaviour](#upstream-behaviour)).

**Success: `200`** (from the local fake upstream; one category and two items shown)

```json
{
  "categories": [
    {
      "category": "Electronics",
      "totalInventoryValue": 16323.5,
      "availableRecordCount": 28,
      "mostRecentlyUpdated": { "id": 1150, "name": "Item MK-1150", "lastUpdated": "2026-09-22T21:50:00Z" },
      "items": [
        { "id": 1005, "name": "Item MK-1005", "price": 17.7, "quantity": 17, "lastUpdated": "2026-09-13T03:25:00Z" },
        { "id": 1010, "name": "Item MK-1010", "price": 49.45, "quantity": 22, "lastUpdated": "2026-09-13T11:30:00Z" }
      ]
    }
  ],
  "summary": {
    "totalRecords": 157,
    "availableRecords": 136,
    "zeroQuantityExcluded": 21,
    "rejectedRecords": [],
    "assumedUtcTimestamps": 0,
    "pagesFetched": 7,
    "requests": 8,
    "generatedAt": "2026-11-20T08:10:40.9118592Z"
  }
}
```

`summary` is an addition to the expected response shape. It shows the completeness checks ran and
accounts for every record: `availableRecords + zeroQuantityExcluded + rejectedRecords.length =
totalRecords`. `requests` counts every request sent upstream, retries included.

**Failure: RFC 9457 Problem Details, never partial figures**

From the fake upstream in `overlap` mode, where page 3 repeats a record and leaves one out:

```json
{
  "type": "urn:inventory-aggregation:incomplete-dataset",
  "title": "The upstream data could not be verified as complete",
  "status": 502,
  "detail": "Expected 157 distinct records, collected 156 after 2 collection attempt(s).",
  "kind": "incomplete-dataset",
  "traceId": "00-2cea6e421249853143a352146a023243-837f7ab8527d578d-00",
  "expectedCount": 157,
  "collectedCount": 156,
  "duplicateIds": [1025, 1050, 1075, 1125]
}
```

`duplicateIds` lists records that arrived twice and were collapsed: 1025, 1075 and 1125 are the
upstream's usual even-page repeats, 1050 is the injected one.

| Status | When |
|---|---|
| `500` | The upstream rejected our configuration (`400`, `401`, `403`, `404`), or a bug in the service |
| `502` | Upstream unreachable or erroring after retries, or its data failed a consistency check |
| `503` | Upstream throttling beyond what the service waits for; carries `Retry-After` |
| `504` | The whole collection exceeded its deadline (60 s by default) |

Every failure has a `kind` (one of the 13 in [design.md §5](openspec/changes/add-inventory-category-report/design.md)),
`type` `urn:inventory-aggregation:{kind}`, and the request's `traceId`. Integrity failures add their
evidence (`page`, `expectedCount`, `collectedCount`, `duplicateIds`, `conflictingIds`, `rejectedRecords`).
Bodies never contain the key, the upstream address or a stack trace (stack traces appear in
`Development` only).

## Telemetry

Logs, traces and metrics go through OpenTelemetry. Nothing leaves the process unless an OTLP endpoint is
set; the startup log says which (`Telemetry export is on: OTLP over grpc` or `... is off`). The standard
`OTEL_*` settings apply:

```bash
export OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317    # a collector, or any OTLP backend
export OTEL_EXPORTER_OTLP_PROTOCOL=grpc                     # or http/protobuf (port 4318)
export OTEL_EXPORTER_OTLP_HEADERS="Authorization=Bearer <token>"   # if the backend needs one
export OTEL_SERVICE_NAME=inventory-aggregation              # the default
```

What is exported:

- **Logs:** everything the service logs (retries, refetches, re-collections, failures with their kind,
  unhandled exceptions with their stack trace), with the trace id of the request.
- **Traces:** one per report: the incoming request, a `collect inventory` span with `inventory.outcome`
  (`success` or the failure kind), `inventory.pages`, `inventory.requests` and `inventory.records`, and a
  span per upstream request.
- **Metrics** on the `InventoryAggregation` meter: `inventory.collections` and
  `inventory.collection.duration` (both by `inventory.outcome`), `inventory.upstream.retries` (by
  `inventory.retry.reason`), `inventory.collection.page_refetches`, `inventory.collection.recollections`;
  plus the ASP.NET Core server metrics.

The upstream address is kept out like a secret: upstream request spans keep the method, status and path
(`/api/records?*`, with the query masked by the runtime) but lose the host, port and full URL, and the
HTTP client's own metrics are not collected because they are tagged with the upstream host. The key is
never in telemetry. Both were checked by exporting a real run to a local OTLP listener and searching the
payloads.

## Run the tests

```bash
dotnet test
```

129 tests, about 10 seconds. No test calls the real upstream. Retries, backoff, timeouts and the rate
limiter run on `FakeTimeProvider`, so a 30-second page timeout takes a fraction of a second. Tests that
cover a spec scenario carry `[Trait("Scenario", "<capability>/<scenario title>")]`; every scenario in the
six specs has at least one.

`dotnet test` runs on Microsoft Testing Platform (set in `global.json`), which xUnit v3 needs on the
.NET 10 SDK. To run one class: `dotnet test --project tests/InventoryAggregation.Api.Tests -- --filter-class "*CategoryReportEndpointTests"`.

## Run against the local fake upstream

`tools/FakeInventoryApi` serves a generated static dataset on `/api/records` with the real upstream's
habits (5 requests per 10 s with `429` beyond it, even pages repeating the previous page's last record,
padded mixed-case categories) and misbehaves in one chosen way per run. It prints the headers of every
request it receives, with the API key redacted. Any non-empty key is accepted.

```bash
dotnet run --project tools/FakeInventoryApi -- --mode drift-totals
```

In a second terminal, point the service at it:

```bash
InventoryApi__BaseUrl=http://localhost:5199/ InventoryApi__ApiKey=local-test-key dotnet run --project src/InventoryAggregation.Api
```

```bash
curl -s http://localhost:5080/api/inventory/categories
```

Run one mode at a time. What the service answers in each, as verified against the stub:

| Mode | The stub | The service answers |
|---|---|---|
| `healthy` | behaves as observed | `200`, 8 requests |
| `flaky-500` | answers `500` to the first request for each page | `200`, 16 requests (each page retried once) |
| `throttle` | answers `429` with `Retry-After: 120` | `503` `upstream-throttled` with `Retry-After: 120`, at once |
| `hang` | never answers page 2 | `502` `upstream-unavailable` after the 30 s page timeout |
| `drift-totals` | page 5 reports 160 records | `502` `inconsistent-paging`, naming page 5 and both values |
| `overlap` | page 3 repeats a record and omits one | `502` `incomplete-dataset`, 157 expected, 156 collected |
| `short-page` | page 3 holds 24 records | `502` `inconsistent-paging` |
| `wrong-page` | page 4 says it is page 3 | `502` `inconsistent-paging` |
| `conflict` | id 1042 appears again with another quantity | `502` `conflicting-records` listing 1042 |
| `bad-record` | id 1099 has a negative price | `200`, with 1099 in `rejectedRecords` ("price is negative") |
| `truncated` | page 2's body ends mid-record | `502` `malformed-response` after 3 fetches |
| `extra-page` | the page after the last holds 3 records | `502` `inconsistent-paging`: the totals understate the data |
| `unknown-status` | answers `418` | `502` `upstream-rejected`, one request |

## Check the specification

```bash
npm ci
npm run spec:check
```

This runs strict OpenSpec validation and `tools/openspec/check-drift.mjs`, which checks that decisions
and sections referenced in the specs exist, that failure kinds and settings match between specs, design
and code, and that every scenario has a test. It runs in CI on every push and pull request, and before
each commit once the hook is enabled:

```bash
git config core.hooksPath .githooks
```

## Architecture

```
GET /api/inventory/categories ─▶ CategoryReportEndpoint ─▶ InventoryReportService (shares one run between callers)
                                                             │ collect ▸ validate ▸ aggregate
                                     InventoryCollector ◀────┤ RecordValidator, CategoryAggregator (pure)
                                            │ IInventoryPageSource
                                     InventoryApiClient (typed HttpClient)
                                            │ page timeout ▸ retry ▸ shared rate limiter ▸ attempt timeout
                                       Inventory API
```

Two source projects, split where HTTP stops mattering.

- **`InventoryAggregation.Core`** decides what the numbers are: the record model, validation, the
  collector that walks pages and proves the dataset complete, the aggregation, and the report service.
  It reaches the upstream only through `IInventoryPageSource` ("give me page *n*") and references nothing
  from HTTP, ASP.NET Core, JSON or configuration. The compiler keeps transport out, and a test asserts
  the assembly's references, so all of it is tested in memory against a scripted page source.
- **`InventoryAggregation.Api`** is the host: configuration and its validation, dependency injection
  (`AddInventoryAggregation`), the endpoint, the one failure-to-status map, the exception handler, and
  `InventoryApiClient`, the typed client that implements `IInventoryPageSource`.

Faults are handled in two layers because they are different faults:

1. **HTTP layer** (`Microsoft.Extensions.Http.Resilience`, on Polly). It sees status codes, transport
   errors and time: it retries transient failures with exponential backoff and jitter, honours
   `Retry-After` up to a cap, bounds each attempt and each page, and paces every request, retries
   included, through one shared rate limiter so throttling stays the exception.
2. **Dataset layer** (the collector). A `200` can still be wrong. Each page is checked against page 1's
   totals and the page asked for; a page that disagrees is fetched again; the page after the last is
   requested to confirm the end; and the count of distinct ids must equal the reported total. The data
   is static, so one full re-collection separates a glitch from a real fault before giving up.

Expected failures travel as values (a page or a classified failure; a dataset or a classified failure),
not exceptions, so the endpoint maps each to a status in one place and nothing unhandled reaches a
caller. The full design, with the reasoning behind every choice, is in
[`design.md`](openspec/changes/add-inventory-category-report/design.md).

## Decisions and assumptions

Where the requirements leave something open, this is what the service does. Numbers refer to the
decision table in `design.md` §4.

- **Two projects** (D1): Core with no HTTP, Api with the host and the upstream client.
- **Two layers of resilience** (D2), because a `200` with wrong content passes every HTTP policy.
- **Page size 25** (D3), the upstream's maximum, so the fewest requests and chances to fail.
- **Page 1 first, then up to 4 pages at once** (D4). Page 1 supplies the totals; the rate limit, not the
  concurrency, sets the total time.
- **`Retry-After`** is honoured up to 10 s (D5). Asked to wait longer, the service answers `503` at once
  and passes the upstream's `Retry-After` on.
- **An unreadable `200` body** is fetched again by the collector, not retried by the HTTP layer (D6).
- **No partial results** (D7). If completeness cannot be proven, the caller gets an error.
- **End of data** is confirmed by asking for one page past the last; an empty page or a `400` both mean
  "no more" (D8).
- **Duplicates** (D9). A record repeated with identical content counts once. The same id with different
  content is fetched again and, if it persists, fails the report; the service does not guess which is right.
- **Invalid records** (D10), such as a missing field, a negative price or quantity, or an unreadable
  timestamp, are left out of every figure and listed in `summary.rejectedRecords` with every reason.
  `InventoryApi:InvalidRecordPolicy=Fail` refuses the whole report instead.
- **Categories** (D11) group on the name with surrounding spaces removed, ignoring case. The name shown is
  the most common spelling in the group, ties broken by ordinal order.
- **Zero quantity** (D12). Records with quantity 0 count toward nothing. A category whose records all
  have quantity 0 is left out; the count of such records is in `summary`. Negative quantity is invalid.
- **Money** (D13) is `decimal` throughout. `price × quantity` is summed exactly and rounded once, to 2
  places, half away from zero.
- **Timestamps** (D14) are compared as UTC instants. A value without an offset is read as UTC and counted
  in `summary.assumedUtcTimestamps`. Most recently updated is the latest; ties go to the lowest id.
- **Ordering** (D15): categories by name, records within a category by id.
- **Status codes** (D16) say whose fault a failure is and whether retrying helps (table above).
- **No caching** (D17) and **no database** (D23). Every report reads the upstream fresh.
- **Response shape** (D18): the expected shape plus `summary`. Item prices are echoed as the upstream
  sent them; only computed money is rounded.
- **OpenAPI and Swagger UI** (D19): the built-in document at `/openapi/v1.json` and Swagger UI at `/swagger`
  reading it, both in `Development` only.
- **One re-collection** (D20) before an integrity failure is reported.
- **Overlapping pages** (D21). A page may hold more records than the page size; only fewer is a fault.
- **Pacing** (D22). One rate limiter for the whole app allows 5 requests per 10 s, retries included.
  Each permit comes back a full window after its request finished, so no 10-second window, wherever the
  upstream's boundaries fall, sees more than 5 of our requests.
- **Shared collections** (D24). A request arriving while a collection runs waits for that one; nothing
  is kept after it ends. The shared run stops only when every caller waiting on it has gone.
- **Outbound requests** carry only `Host` and the key header: no User-Agent, no trace-context headers.
  Default request logging is off because it writes the upstream URL; retries are logged by page instead.
- **Telemetry** (D25) goes through OpenTelemetry, exported over OTLP only when an endpoint is configured,
  without the upstream address or the key.

## Upstream behaviour

Recorded with `tools/probe` before any code was written. Details in
[`probe-findings.md`](openspec/changes/add-inventory-category-report/probe-findings.md).

- **Rate limit:** 5 requests per 10 seconds, then `429` with `Retry-After` in seconds.
- **Overlapping pages:** every even-numbered page repeats the previous page's last record, so it carries
  one record more than the page size. Nothing is ever missing.
- **Messy categories:** names arrive padded and in mixed case (`' Toys'`, `'TOYS'`, `'toys '`).
- **157 records, ids 1001–1157,** identical across every pass and page size; 21 have quantity 0, among
  them all 7 in a `Discontinued` category.
- **Errors** are Problem Details. A missing key and a wrong key get the same `401`. Out-of-range paging
  gets `400`; past the last page gets an empty `200`.

## Known limitations and next steps

- **About 10 seconds per report.** The upstream's rate limit, not the service, sets this. Concurrent
  callers share one collection, but each new report pays it again.
- **One instance.** The shared collection (D24) and the rate limiter (D22) live in memory. Several
  instances would each collect separately and compete for the same upstream limit. Next step: a shared
  limiter (for example in Redis) or a short-lived shared result.
- **No caching, no persistence, no last-good fallback** (D17, D23). When the upstream is down, callers get
  an error, not the last good report. Adding one needs storage and a way to show how old the data is.
- **Waiting for a permit counts toward the page timeout.** With one collection per instance the longest
  wait is one window (10 s) against a 30 s page timeout. Two collections can only overlap briefly, when
  every caller of one has left and a new one starts while it winds down; the new one's last pages could
  then wait long enough to fail. Next step: exclude permit waits from the page timeout and bound them by
  the collection deadline alone.
- **Faults not seen in practice.** `500`s, timeouts, malformed bodies and conflicting records are
  handled and tested with synthetic faults and the fake upstream, but were never observed from the real
  upstream.
- **Records without an id** cannot be told apart, so reconciliation counts each one as its own record
  (and validation rejects it). A repeated id-less record would therefore count twice.
- **Startup errors** are printed cleanly, but the host also logs the validation exception with a stack
  trace first.
- **Telemetry is only as good as its backend.** Export is off until `OTEL_EXPORTER_OTLP_ENDPOINT` is set, and
  no alerts or dashboards ship with the service. Only the caller that started a shared collection sees the
  `collect inventory` span in its trace; callers that joined it do not.
- **Category display name** follows the most common spelling in the data. It is deterministic, but a
  shift in the upstream's mix of spellings could change the name shown.
- **Test clock.** Tests run timeouts, backoff and pacing on `FakeTimeProvider`; the helper that drives
  the fake clock pauses about a millisecond between steps so thread-pool continuations run before time
  moves on. No test waits for a simulated delay in real time.
- **The endpoint has no authentication of its own.** Callers are trusted; put it behind the platform's
  gateway or add authentication before exposing it.

## Repository layout

```
src/InventoryAggregation.Core      records, validation, collection, aggregation, report service (no HTTP)
src/InventoryAggregation.Api       host, endpoint, failure mapping, upstream client and its resilience
tests/                             Core tests (scripted page source) and Api tests (scripted HTTP, WebApplicationFactory)
tools/FakeInventoryApi/            local fake upstream with fault modes
tools/openspec/                    drift checker used by spec:check
openspec/                          project context and the specification change (proposal, design, specs, tasks, probe findings)
.github/workflows/, .githooks/     spec checks in CI and before each commit
recordings/                        (git-ignored) raw upstream responses and the expected report
```
