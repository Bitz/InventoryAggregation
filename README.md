# Inventory Aggregation Service

An inventory reporting service. It reads every inventory record from the upstream Inventory API, checks
that what it collected is complete and consistent, groups the records by product category, and serves
the figures from one HTTP endpoint: what is on hand, what it is worth, and what changed last.

A figure computed from a partial or inconsistent dataset is worse than no figure, so the service never
shows one. It refreshes the report in the background and serves the latest report it has proven
complete, saying how old it is and whether the latest refresh failed.

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
[design.md §8](openspec/changes/archive/2026-11-20-serve-last-good-report/design.md). A missing or out-of-range setting
stops the service at startup with a message that names it, for example:

```
InventoryApi:PageSize is 50; it must be from 1 to 25.
```

The refresh settings, in the same section:

| Setting | Default | Meaning |
|---|---|---|
| `RefreshInterval` | `00:05:00` | from one scheduled refresh start to the next; at least `CollectionTimeout` |
| `RefreshRetryDelay` | `00:00:30` | wait after a failed refresh, doubling per further failure up to `RefreshInterval` |
| `StaleAfter` | `01:00:00` | a report older than this is served with `overdue: true`; at least `RefreshInterval` |
| `FreshMaxAge` | `00:00:30` | `fresh=true` is answered from the store when the report is younger than this |

## Run

```bash
dotnet run --project src/InventoryAggregation.Api
```

The service listens on `http://localhost:5080`. In `Development`, Swagger UI is at
`http://localhost:5080/swagger` and the OpenAPI document it renders is at `http://localhost:5080/openapi/v1.json`.
Neither is served in other environments.

## Call the endpoint

```bash
curl -s -i http://localhost:5080/api/inventory/categories
```

The answer comes from memory at once. The service builds the report in the background: once when it
starts and then every 5 minutes. A build takes a little over 10 seconds against the real upstream,
because it allows 5 requests per 10 seconds and a full read needs 8 (see
[Upstream behaviour](#upstream-behaviour)). Requests in the first 10 seconds after a start get
`503 report-not-ready` (below).

**Success: `200`** (from the local fake upstream, with `RefreshInterval` set to 1 minute; one category and two items shown)

```
HTTP/1.1 200 OK
Content-Type: application/json; charset=utf-8
Age: 1
```

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
    "generatedAt": "2026-11-20T17:32:34.0008382Z"
  },
  "freshness": {
    "asOf": "2026-11-20T17:32:34.0008382Z",
    "ageSeconds": 1,
    "trigger": "startup",
    "stale": false,
    "overdue": false,
    "lastRefresh": { "startedAt": "2026-11-20T17:32:23Z", "outcome": "success" },
    "nextRefreshAt": "2026-11-20T17:33:23.8108797Z"
  }
}
```

`summary` and `freshness` are additions to the expected response shape. `summary` shows the
completeness checks ran and accounts for every record: `availableRecords + zeroQuantityExcluded +
rejectedRecords.length = totalRecords`. `requests` counts every request sent upstream, retries included.

`freshness` says how old the figures are and whether refreshing is working:

| Field | Meaning |
|---|---|
| `asOf` | when the figures were made; equals `summary.generatedAt` |
| `ageSeconds` | seconds since `asOf`, also sent as the standard `Age` header |
| `trigger` | what built this report: `startup`, `scheduled` or `fresh` |
| `stale` | the latest refresh, which came after this report, failed |
| `overdue` | the report is older than `StaleAfter` (1 hour) |
| `lastRefresh` | the latest attempt: `startedAt` (whole seconds), `outcome` (`success` or a failure kind) and, for a failure, `detail` |
| `nextRefreshAt` | when the next scheduled refresh, or retry, is due |

**When the upstream fails after a good report: still `200`.** The service keeps the report it had and
marks it stale. From the fake upstream switched to `throttle` a minute after a good report:

```json
"freshness": {
  "asOf": "2026-11-20T17:32:34.0008382Z",
  "ageSeconds": 50,
  "trigger": "startup",
  "stale": true,
  "overdue": false,
  "lastRefresh": {
    "startedAt": "2026-11-20T17:33:23Z",
    "outcome": "upstream-throttled",
    "detail": "The Inventory API is throttling requests (status 429) for page 1: asked to wait 120 s, longer than the 10 s the service waits."
  },
  "nextRefreshAt": "2026-11-20T17:33:33.8360887Z"
}
```

The categories and summary are exactly those of the good report. A caller that used to read a `5xx` to
detect upstream trouble reads `freshness.stale` and `freshness.lastRefresh.outcome` instead.

**A live report: `fresh=true`.** The caller waits for a collection (about 10 s), joining one already
running. If the stored report is younger than 30 s (`FreshMaxAge`) it is returned as is, so repeated
forced calls cannot spend the upstream's rate limit. A successful forced collection replaces the stored
report for everyone; a failed one returns the stored report marked stale. A caller that hangs up stops
waiting; the collection carries on and its result serves everyone after.

```bash
curl -s "http://localhost:5080/api/inventory/categories?fresh=true"
```

**No good report yet: `503 report-not-ready`.** The one error an upstream problem can cause, while no
good report has been produced since the service started. From the fake upstream with a wrong key:

```
HTTP/1.1 503 Service Unavailable
Content-Type: application/problem+json
Retry-After: 37
```

```json
{
  "type": "urn:inventory-aggregation:report-not-ready",
  "title": "No inventory report is available yet",
  "status": 503,
  "detail": "No report has been produced since the service started; the last attempt failed with upstream-misconfigured.",
  "kind": "report-not-ready",
  "traceId": "00-2bcd7b1e8d3b89afe67618bd1f842611-7902bab98a74bbbb-03",
  "lastRefresh": {
    "startedAt": "2026-11-20T17:34:48Z",
    "outcome": "upstream-misconfigured",
    "detail": "The Inventory API answered 401: the API key is missing or rejected. Check InventoryApi:ApiKey."
  }
}
```

`Retry-After` is 10 s (the upstream's rate-limit window) while a refresh is running, otherwise the time
until the next attempt plus 10 s. `lastRefresh` is absent while the first refresh is still running.

| Status | When |
|---|---|
| `200` | A good report is stored, however old, and whether or not the latest refresh failed |
| `400` | The query cannot be read, for example `fresh=maybe` |
| `500` | A bug in the service (`kind: internal-error`) |
| `503` | No good report since the service started (`kind: report-not-ready`), with `Retry-After` |

The failure kinds a refresh can end with (13, in [design.md §5](openspec/changes/archive/2026-11-20-serve-last-good-report/design.md)) appear as
`lastRefresh.outcome`, in the log and in metrics; they are no longer response statuses. Problem Details
bodies carry `type` `urn:inventory-aggregation:{kind}` and the request's `traceId`, and never contain
figures, the key, the upstream address or a stack trace (stack traces appear in `Development` only).

## Run audit (lineage)

The service is an aggregating ETL pipeline, and every run can account for itself. Ask for the audit with
`include=audit`; without it the response is unchanged. The audit is that of the run that produced the
served report, so a stale report comes with the audit of the good run behind it:

```bash
curl -s "http://localhost:5080/api/inventory/categories?include=audit"
```

The `audit` block (from the fake upstream in `bad-record` mode; lists shortened):

```json
"audit": {
  "runId": "4b334d",
  "startedAt": "2026-11-20T09:04:23.1238701Z",
  "finishedAt": "2026-11-20T09:04:33.2856838Z",
  "fingerprint": "sha256:4b6c530aa25b56062619c303c0ec37e3929126bd97cd17a870d29a720a7a5918",
  "extract": {
    "reportedTotalRecords": 157, "reportedTotalPages": 7, "recordsReceived": 160, "distinctRecords": 157,
    "requests": 8, "collectionAttempts": 1, "pageRefetches": [], "recollections": [],
    "collapsedDuplicates": [ { "id": 1025, "pages": [1, 2] }, { "id": 1075, "pages": [3, 4] }, { "id": 1125, "pages": [5, 6] } ]
  },
  "transform": {
    "categories": [ { "category": "Toys", "spellings": [ { "spelling": "  toys ", "records": 5 }, { "spelling": " Toys", "records": 5 } ] } ],
    "timestamps": { "convertedToUtc": [], "assumedUtc": [] },
    "rejected": [ { "id": 1099, "reasons": ["price is negative"] } ],
    "zeroQuantityExcluded": [ { "category": "Discontinued", "ids": [1151, 1152, 1153, 1154, 1155, 1156, 1157] } ]
  },
  "load": {
    "categories": [
      { "category": "Discontinued", "recordsIn": 7, "zeroQuantityExcluded": 7, "recordsOut": 0 },
      { "category": "Toys", "recordsIn": 29, "zeroQuantityExcluded": 3, "recordsOut": 26 }
    ],
    "recordsIn": 156, "rejected": 1, "zeroQuantityExcluded": 21, "recordsOut": 135
  }
}
```

- **Extract** is what was pulled: the upstream's totals, records received including the even-page
  repeats, distinct records, requests, every page refetch with the check that failed, every re-collection
  with its reason, and each collapsed duplicate with the pages it arrived on.
- **Transform** is every correction: per category, each raw spelling and how many records used it (all of
  them become the category name shown), timestamps converted from an offset or read as UTC, rejected
  records with reasons, and zero-quantity exclusions per category, including categories that drop out.
- **Load** is what fed the figures: per category, records in = zero-quantity excluded + records out;
  overall, records in + rejected = `summary.totalRecords`.
- **Fingerprint** is a SHA-256 over the distinct records exactly as received, ordered by id. The upstream
  data is meant to be static, so the fingerprint should stay the same from run to run; a new value means
  the data changed. The service keeps only the latest good report (D28), so compare fingerprints in the
  log or telemetry backend.

Every run, successful or not, also writes one structured **run manifest** record (event `RunManifest`,
or `RunManifestWithoutData` when nothing was loaded) whose properties carry the run id, trace id,
outcome, fingerprint and the extract, transform and load counts. It goes wherever logs go, including OTLP:

```
report 5529d0: run manifest: outcome success, fingerprint sha256:6f4896e5…; extract: 160 received, 157 distinct,
3 duplicates collapsed, 0 refetches, 0 re-collections; transform: 0 rejected, 21 zero-quantity, 0 converted to UTC,
0 assumed UTC, 112 spellings normalized; load: 136 records out; 10.2 s; trace 5529d02e729bd22540224911a4277b0b
```

## Progress in the console

Each refresh logs its progress at `Information` level, one line per upstream page, prefixed with a short
id of the collection (the start of its trace id):

```
report 7f3a91: page 1/7: 25 records (1 attempt(s), 180 ms)
report 7f3a91: page 2/7: 26 records (1 attempt(s), 175 ms)
report 7f3a91: page 6 waited 9.8 s for a rate-limit permit
report 7f3a91: page 8 (end check): 0 records, end confirmed (1 attempt(s), 160 ms)
report 7f3a91: collected 157 records over 7 pages in 8 requests, 10.4 s (0 page refetches, 1 collection attempts)
```

A failed fetch shows its failure kind and status instead of a record count. Retries, refetches and
failures still log at `Warning`, and so does a failed refresh, saying the previous report is kept:

```
Refresh (scheduled) failed with upstream-throttled: The Inventory API is throttling requests (status 429) for page 1: asked to wait 120 s, longer than the 10 s the service waits. The previous report, generated 2026-11-20T17:32:34.0008382+00:00, is kept and served as stale
2 refresh(es) failed in a row; next attempt in 20 s
```
 No line carries the upstream address or the key. To quiet the progress
lines, raise the level for the service's categories, for example
`Logging__LogLevel__InventoryAggregation=Warning`.

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
  unhandled exceptions with their stack trace), with the trace id of the refresh or request.
- **Traces:** one per refresh: a root span `refresh inventory report` with `inventory.trigger`
  (`startup`, `scheduled` or `fresh`), a `collect inventory` span beneath it with `inventory.outcome`
  (`success` or the failure kind), `inventory.pages`, `inventory.requests` and `inventory.records`, and a
  span per upstream request. Every `200` links its server span to the `refresh inventory report` span of
  the refresh that produced the served report, stale or not, so a caller's trace leads to the run behind
  its figures. A request with `fresh=true` that waited on a collection that failed links to that one too.
- **Metrics** on the `InventoryAggregation` meter: `inventory.collections` and
  `inventory.collection.duration` (both by `inventory.outcome` and `inventory.trigger`),
  `inventory.report.age` (seconds since the stored report was made; nothing while none is stored),
  `inventory.upstream.retries` (by
  `inventory.retry.reason`), `inventory.collection.page_refetches`, `inventory.collection.recollections`, and the data-quality counters `inventory.records.rejected` (by
  `inventory.rejection.reason`), `inventory.records.duplicates_collapsed` and `inventory.records.spellings_normalized`;
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

189 tests, about 15 seconds. No test calls the real upstream. Retries, backoff, timeouts, the rate
limiter and the refresh schedule run on `FakeTimeProvider`, so a 30-second page timeout or a 5-minute
refresh interval takes a fraction of a second. Tests that cover a spec scenario carry
`[Trait("Scenario", "<capability>/<scenario title>")]`; every scenario in the specs has at least one.

`dotnet test` runs on Microsoft Testing Platform (set in `global.json`), which xUnit v3 needs on the
.NET 10 SDK. To run one class: `dotnet test --project tests/InventoryAggregation.Api.Tests -- --filter-class "*CategoryReportEndpointTests"`.

## Run against the local fake upstream

`tools/FakeInventoryApi` serves a generated static dataset on `/api/records` with the real upstream's
habits (5 requests per 10 s with `429` beyond it, even pages repeating the previous page's last record,
padded mixed-case categories) and misbehaves in one chosen way at a time. It prints the headers of every
request it receives, with the API key redacted. Any non-empty key is accepted unless `--key` names the
one to accept.

```bash
dotnet run --project tools/FakeInventoryApi -- --mode healthy
```

In a second terminal, point the service at it:

```bash
InventoryApi__BaseUrl=http://localhost:5199/ InventoryApi__ApiKey=local-test-key dotnet run --project src/InventoryAggregation.Api
```

Then switch the fake's mode while both run, and watch the stored report go stale and recover:

```bash
curl -s http://localhost:5080/api/inventory/categories           # 200, stale: false
curl -s -X POST http://localhost:5199/fake/mode/throttle          # the upstream starts failing
curl -s http://localhost:5080/api/inventory/categories           # after the next refresh: 200, the same figures, stale: true
curl -s -X POST http://localhost:5199/fake/mode/healthy           # the upstream recovers
curl -s http://localhost:5080/api/inventory/categories           # after the next retry: 200, stale: false, a new asOf
```

`InventoryApi__RefreshInterval=00:01:00` and `InventoryApi__RefreshRetryDelay=00:00:10` shorten the
wait. To see the one error, start the fake with `--key local-good-key` and the service with any other
key: every request gets `503 report-not-ready` with `lastRefresh.outcome` `upstream-misconfigured`.

What each mode does to a refresh, as verified against the fake:

| Mode | The fake | The refresh ends with |
|---|---|---|
| `healthy` | behaves as observed | `success`, 8 requests |
| `flaky-500` | answers `500` to the first request for each page | `success`, 16 requests (each page retried once) |
| `throttle` | answers `429` with `Retry-After: 120` | `upstream-throttled`, at once |
| `hang` | never answers page 2 | `upstream-unavailable` after the 30 s page timeout |
| `drift-totals` | page 5 reports 160 records | `inconsistent-paging`, naming page 5 and both values |
| `overlap` | page 3 repeats a record and omits one | `incomplete-dataset`, 157 expected, 156 collected |
| `short-page` | page 3 holds 24 records | `inconsistent-paging` |
| `wrong-page` | page 4 says it is page 3 | `inconsistent-paging` |
| `conflict` | id 1042 appears again with another quantity | `conflicting-records` listing 1042 |
| `bad-record` | id 1099 has a negative price | `success`, with 1099 in `rejectedRecords` ("price is negative") |
| `truncated` | page 2's body ends mid-record | `malformed-response` after 3 fetches |
| `extra-page` | the page after the last holds 3 records | `inconsistent-paging`: the totals understate the data |
| `unknown-status` | answers `418` | `upstream-rejected`, one request |

After a good report, a failing mode gives `200` with `stale: true` and that outcome in `lastRefresh`;
from a cold start it gives `503 report-not-ready` with the outcome in `lastRefresh`.

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
 startup, every 5 min ─▶ ReportRefreshService ─┐
                                                ▼
 GET ?fresh=true ─▶ CategoryReportEndpoint ─▶ ReportRefresher (one collection at a time, schedule, retries)
 GET ─────────────▶ CategoryReportEndpoint      │                         │ success only
        ▲ reads                                 ▼                         ▼
        └──────────────────────────────── ReportStore (latest good report + last attempt, in memory)
                                         InventoryReportService
                                                │ collect ▸ validate ▸ aggregate
                    InventoryCollector ◀────────┤ RecordValidator, CategoryAggregator (pure)
                           │ IInventoryPageSource
                    InventoryApiClient (typed HttpClient)
                           │ page timeout ▸ retry ▸ shared rate limiter ▸ attempt timeout
                      Inventory API
```

Two source projects, split where HTTP stops mattering.

- **`InventoryAggregation.Core`** decides what the numbers are and when they are made: the record model,
  validation, the collector that walks pages and proves the dataset complete, the aggregation, the report
  service, the store that holds the latest good report, and the refresher that schedules collections and
  shares the one in flight.
  It reaches the upstream only through `IInventoryPageSource` ("give me page *n*") and references nothing
  from HTTP, ASP.NET Core, JSON or configuration. The compiler keeps transport out, and a test asserts
  the assembly's references, so all of it is tested in memory against a scripted page source.
- **`InventoryAggregation.Api`** is the host: configuration and its validation, dependency injection
  (`AddInventoryAggregation`), the hosted service that runs the refresh schedule, the endpoint, the one
  failure-to-status map, the exception handler, and `InventoryApiClient`, the typed client that
  implements `IInventoryPageSource`.

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
not exceptions. A failed refresh is recorded in the store next to the report it leaves in place, so a
caller sees it as `freshness.lastRefresh` and nothing unhandled reaches a caller. The design, with the
reasoning behind every choice, is in [`design.md`](openspec/changes/archive/2026-11-20-add-inventory-category-report/design.md) (D1–D26) and
[`design.md`](openspec/changes/archive/2026-11-20-serve-last-good-report/design.md) (the background refresh, D27–D36, and the decisions it revises).

## Decisions and assumptions

Where the requirements leave something open, this is what the service does. Numbers refer to the
decision tables in the two archived `design.md` files, §4.

- **Two projects** (D1): Core with no HTTP, Api with the host and the upstream client.
- **Two layers of resilience** (D2), because a `200` with wrong content passes every HTTP policy.
- **Page size 25** (D3), the upstream's maximum, so the fewest requests and chances to fail.
- **Page 1 first, then up to 4 pages at once** (D4). Page 1 supplies the totals; the rate limit, not the
  concurrency, sets the total time.
- **`Retry-After`** is honoured up to 10 s (D5). Asked to wait longer, the refresh ends at once with
  `upstream-throttled`.
- **An unreadable `200` body** is fetched again by the collector, not retried by the HTTP layer (D6).
- **No partial results** (D7). A collection that cannot prove completeness never produces figures and
  never replaces the stored report. The caller gets the last good report, marked stale, instead of an
  error.
- **End of data** is confirmed by asking for one page past the last; an empty page or a `400` both mean
  "no more" (D8).
- **Duplicates** (D9). A record repeated with identical content counts once. The same id with different
  content is fetched again and, if it persists, fails the report; the service does not guess which is right.
- **Invalid records** (D10), such as a missing field, a negative price or quantity, or an unreadable
  timestamp, are left out of every figure and listed in `summary.rejectedRecords` with every reason.
  `InventoryApi:InvalidRecordPolicy=Fail` fails the whole refresh instead, and the stored report is kept.
- **Categories** (D11) group on the name with surrounding spaces removed, ignoring case. The name shown is
  the most common spelling in the group, ties broken by ordinal order.
- **Zero quantity** (D12). Records with quantity 0 count toward nothing. A category whose records all
  have quantity 0 is left out; the count of such records is in `summary`. Negative quantity is invalid.
- **Money** (D13) is `decimal` throughout. `price × quantity` is summed exactly and rounded once, to 2
  places, half away from zero.
- **Timestamps** (D14) are compared as UTC instants. A value without an offset is read as UTC and counted
  in `summary.assumedUtcTimestamps`. Most recently updated is the latest; ties go to the lowest id.
- **Ordering** (D15): categories by name, records within a category by id.
- **Status codes** (D16). Collection failures are no longer statuses; they show as
  `lastRefresh.outcome`. The endpoint answers `200`, `400`, `500` (a bug) or `503 report-not-ready`.
- **Response shape** (D18, D34): the expected shape plus `summary` and `freshness`, and an `Age` header.
  Item prices are echoed as the upstream sent them; only computed money is rounded.
- **OpenAPI and Swagger UI** (D19): the built-in document at `/openapi/v1.json` and Swagger UI at `/swagger`
  reading it, both in `Development` only.
- **One re-collection** (D20) before an integrity failure is reported.
- **Overlapping pages** (D21). A page may hold more records than the page size; only fewer is a fault.
- **Pacing** (D22). One rate limiter for the whole app allows 5 requests per 10 s, retries included.
  Each permit comes back a full window after its request finished, so no 10-second window, wherever the
  upstream's boundaries fall, sees more than 5 of our requests.
- **One collection at a time** (D24). Startup, scheduled and forced refreshes that find a collection
  running wait for its outcome instead of starting another.
- **Outbound requests** carry only `Host` and the key header: no User-Agent, no trace-context headers.
  Default request logging is off because it writes the upstream URL; retries are logged by page instead.
- **Run audit** (D26): every run builds its lineage (extract, transform, load, fingerprint). The response
  carries it with `?include=audit`; every run logs it as a structured manifest record.
- **Telemetry** (D25) goes through OpenTelemetry, exported over OTLP only when an endpoint is configured,
  without the upstream address or the key.
- **Served from the latest good report** (D27). A background refresh builds the report; a request never
  waits for the upstream unless it asks with `fresh=true`. This replaces "no caching" (D17).
- **In memory only** (D28). No database or file (D23 still holds for disk). A restart starts empty until
  the first refresh succeeds, about 10 s when the upstream is healthy.
- **Nothing to show yet** (D29) is the one error: `503 report-not-ready`, with `Retry-After` and the last
  attempt's outcome, so an operator can tell a down upstream from a wrong key.
- **Every 5 minutes; overdue after an hour** (D30). An hour-old good report is still served, marked
  `overdue`.
- **`fresh=true`** (D31) waits for a live collection, unless the stored report is under 30 s old. A
  forced success replaces the stored report for everyone and does not move the schedule.
- **Retrying a failed refresh** (D32) after 30 s, doubling to at most 5 minutes; back to the 5-minute
  schedule after a success.
- **Collections finish** (D33) whoever is waiting, bounded by `CollectionTimeout`; a caller that leaves
  only stops waiting. Shutdown cancels the running collection and records no failure.
- **`freshness` and `Age`** (D34, D37) on every `200`, so callers can tell how old the figures are without
  reading logs.
- **A trace per refresh** (D35), and every `200` links to the refresh that produced its report (D38).
- **The refresh settings** (D36) sit in the `InventoryApi` section with the others.

## Upstream behaviour

Recorded with `tools/probe` before any code was written. Details in
[`probe-findings.md`](openspec/changes/archive/2026-11-20-add-inventory-category-report/probe-findings.md).

- **Rate limit:** 5 requests per 10 seconds, then `429` with `Retry-After` in seconds.
- **Overlapping pages:** every even-numbered page repeats the previous page's last record, so it carries
  one record more than the page size. Nothing is ever missing.
- **Messy categories:** names arrive padded and in mixed case (`' Toys'`, `'TOYS'`, `'toys '`).
- **157 records, ids 1001–1157,** identical across every pass and page size; 21 have quantity 0, among
  them all 7 in a `Discontinued` category.
- **Errors** are Problem Details. A missing key and a wrong key get the same `401`. Out-of-range paging
  gets `400`; past the last page gets an empty `200`.

## Known limitations and next steps

- **Figures are up to 5 minutes old when the upstream is healthy**, and older while it is failing.
  `freshness` and the `Age` header always say how old; `fresh=true` gets current figures at the cost of
  about 10 s.
- **In memory only** (D28). A restart forgets the report, and requests get `503 report-not-ready` for
  about 10 s, longer if the upstream is failing at that moment. Keeping the last good report on disk (a
  JSON file or SQLite) would close that gap and is the next step.
- **Each instance refreshes on its own.** Every instance runs its own schedule and its own rate limiter,
  so upstream load grows with the instance count: 8 requests per 5 minutes each, well inside the limit
  for a handful, but instances can briefly show reports a few minutes apart. A single shared refresher
  writing to a shared store is the step after that.
- **A forced success during backoff resets the schedule** to 5 minutes from that success; a forced
  failure leaves the schedule as it was. A scheduled retry could otherwise fire seconds after a forced
  collection had just succeeded.
- **No limit on the service's own callers.** Plain requests are answered from memory and cost the upstream
  nothing; `fresh=true` is bounded by `FreshMaxAge` and the one collection at a time. ASP.NET Core's
  rate-limiting middleware would protect the service itself.
- **Waiting for a permit counts toward the page timeout.** With one collection at a time the longest
  wait is one window (10 s) against a 30 s page timeout, so this does not bite today. Next step: exclude
  permit waits from the page timeout and bound them by the collection deadline alone.
- **Faults not seen in practice.** `500`s, timeouts, malformed bodies and conflicting records are
  handled and tested with synthetic faults and the fake upstream, but were never observed from the real
  upstream.
- **Records without an id** cannot be told apart, so reconciliation counts each one as its own record
  (and validation rejects it). A repeated id-less record would therefore count twice.
- **Startup errors** are printed cleanly, but the host also logs the validation exception with a stack
  trace first.
- **Telemetry is only as good as its backend.** Export is off until `OTEL_EXPORTER_OTLP_ENDPOINT` is set, and
  no alerts or dashboards ship with the service; an alert on `inventory.report.age` or on
  `inventory.collections` failures would be the first to add.
- **Category display name** follows the most common spelling in the data. It is deterministic, but a
  shift in the upstream's mix of spellings could change the name shown.
- **Test clock.** Tests run timeouts, backoff, pacing and the refresh schedule on `FakeTimeProvider`; the
  helper that drives the fake clock pauses about a millisecond between steps so thread-pool continuations
  run before time moves on, and schedule tests wait until the refresher is parked on its timer before
  moving the clock. No test waits for a simulated delay in real time.
- **The endpoint has no authentication of its own.** Callers are trusted; put it behind the platform's
  gateway or add authentication before exposing it.

## Repository layout

```
src/InventoryAggregation.Core      records, validation, collection, aggregation, report service, store, refresher (no HTTP)
src/InventoryAggregation.Api       host, refresh schedule, endpoint, failure mapping, upstream client and its resilience
tests/                             Core tests (scripted page source) and Api tests (scripted HTTP, WebApplicationFactory)
tools/FakeInventoryApi/            local fake upstream with fault modes
tools/openspec/                    drift checker used by spec:check
openspec/specs/                    the built behaviour, one spec per capability, every scenario tested
openspec/changes/archive/          the archived changes: proposal, design (decisions D1–D26, D27–D36, D37–D39), tasks, probe findings
.github/workflows/, .githooks/     spec checks in CI and before each commit
recordings/                        (git-ignored) raw upstream responses and the expected report
```
