# Inventory Aggregation Service

A small .NET service that reads every inventory record from the upstream Inventory API, checks that what
it collected is complete and consistent, groups the records by category, and serves the result from one
HTTP endpoint.

> **Status: specified, not yet built.** The specification, the upstream probe and the spec checks are in
> place; `src/` and `tests/` do not exist yet. Sections marked *(once built)* describe the service as
> specified. They will be confirmed against the running code, and this note removed, when it lands.

## Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| .NET SDK | 10.0 | building, running and testing the service |
| Node.js | 20 or later | spec validation (`npm run spec:check`) only |
| PowerShell | 7.2 or later | the upstream probe in `tools/probe` only |

## Configure the upstream

The service needs the upstream's base URL and an API key. Neither is stored in the repository.

**Locally: .NET user secrets.** The store id is fixed, so this works before and after the projects exist:

```bash
dotnet user-secrets set "InventoryApi:BaseUrl" "<base URL>" --id inventory-aggregation-7d3f2c1e
dotnet user-secrets set "InventoryApi:ApiKey" "<API key>" --id inventory-aggregation-7d3f2c1e
```

Check what is set without printing values:

```bash
dotnet user-secrets list --id inventory-aggregation-7d3f2c1e
```

**Elsewhere: environment variables.** These take precedence over user secrets:

```bash
export InventoryApi__BaseUrl="<base URL>"
export InventoryApi__ApiKey="<API key>"
```

The key is sent in the `X-Api-Key` header. Every other setting has a default in `appsettings.json`; the
full list with ranges is in [design.md §8](openspec/changes/add-inventory-category-report/design.md).
*(Once built)* the service refuses to start when a setting is missing or out of range, and names it.

## Run *(once built)*

```bash
dotnet run --project src/InventoryAggregation.Api
```

The console prints the address it listens on.

## Call the endpoint *(once built)*

```bash
curl -s http://localhost:<port>/api/inventory/categories
```

A report takes a little over 10 seconds, because the upstream allows 5 requests per 10 seconds and a full
read needs 8 (see [Upstream behaviour](#upstream-behaviour)).

**Success: `200`**

```json
{
  "categories": [
    {
      "category": "Electronics",
      "totalInventoryValue": 1799.88,
      "availableRecordCount": 1,
      "mostRecentlyUpdated": { "id": 1001, "name": "Thermal Receipt Printer", "lastUpdated": "2026-10-15T12:30:00Z" },
      "items": [
        { "id": 1001, "name": "Thermal Receipt Printer", "price": 149.99, "quantity": 12, "lastUpdated": "2026-10-15T12:30:00Z" }
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
    "generatedAt": "2026-11-20T15:04:05Z"
  }
}
```

`summary` is an addition to the expected response shape. It shows the completeness checks ran and
accounts for every record: `availableRecords + zeroQuantityExcluded + rejectedRecords.length =
totalRecords`.

**Failure: RFC 9457 Problem Details, never partial figures**

```json
{
  "type": "urn:inventory-aggregation:incomplete-dataset",
  "title": "The upstream data could not be verified as complete",
  "status": 502,
  "detail": "Expected 157 distinct records, collected 156 after 2 attempts.",
  "kind": "incomplete-dataset",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"
}
```

| Status | When |
|---|---|
| `500` | The upstream rejected our configuration (`400`, `401`, `403`, `404`), or a bug in the service |
| `502` | Upstream unreachable or erroring after retries, or its data failed a consistency check |
| `503` | Upstream throttling beyond what the service waits for; carries `Retry-After` |
| `504` | The whole collection exceeded its deadline (60 s by default) |

*(Once built)* the OpenAPI document is at `/openapi/v1.json` in the Development environment.

## Run the tests *(once built)*

```bash
dotnet test
```

No test calls the real upstream or waits on real time; retries and timeouts run on a fake clock.

## Check the specification

```bash
npm ci
npm run spec:check
```

This runs strict OpenSpec validation and `tools/openspec/check-drift.mjs`, which checks that decisions
and sections referenced in the specs exist, that failure kinds and settings match between specs, design
and code, and that every scenario of built behaviour has a test (tests name scenarios with
`[Trait("Scenario", "<capability>/<scenario title>")]`). It runs in CI on every push and pull request, and
before each commit once the hook is enabled:

```bash
git config core.hooksPath .githooks
```

## Architecture

Two source projects, split where HTTP stops mattering.

- **`InventoryAggregation.Core`** holds everything that decides what the numbers are: the record model,
  validation, the collector that walks pages and proves the dataset complete, and the aggregation. It
  talks to the upstream only through `IInventoryPageSource` ("give me page *n*") and references nothing
  from HTTP or ASP.NET, so the compiler keeps transport concerns out and all of it is testable in memory.
- **`InventoryAggregation.Api`** is the host: configuration, dependency injection, the endpoint, the
  mapping from failures to status codes, and `InventoryApiClient`, the typed `HttpClient` from
  `IHttpClientFactory` that implements `IInventoryPageSource`.

Faults are handled in two layers, because they are different kinds of fault:

1. **HTTP layer** (`Microsoft.Extensions.Http.Resilience`, built on Polly): retries transient failures
   (`408`, `429`, `5xx`, transport errors, timeouts) with exponential backoff and jitter, honours
   `Retry-After`, and bounds each attempt and each page. A shared rate limiter in front of it keeps the
   service inside the upstream's limit, so throttling is the exception rather than the norm.
2. **Dataset layer** (the collector): a `200` can still be wrong. Each page is checked against page 1's
   totals and the page asked for; a disagreeing page is fetched again; the page after the last is
   requested to confirm the end; and the distinct record count must equal the reported total. Because
   the data is static, one full re-collection separates a glitch from a real fault before giving up.

Expected failures travel as values (a page or a classified failure; a dataset or a classified failure),
not exceptions, so nothing unhandled reaches a caller. Concurrent requests share one collection in
flight instead of each spending the rate limit.

The full design, with the reasoning behind every choice, is in
[`openspec/changes/add-inventory-category-report/design.md`](openspec/changes/add-inventory-category-report/design.md).

## Decisions and assumptions

Where the requirements leave something open, this is what the service does.

- **Zero quantity.** Records with quantity 0 count toward nothing. A category whose records are all
  quantity 0 is left out of the response; the count of such records is in `summary`.
- **Invalid records** (missing fields, negative price or quantity, unreadable timestamp) are left out of
  every figure and listed in `summary.rejectedRecords` with reasons. Setting
  `InventoryApi:InvalidRecordPolicy` to `Fail` refuses the whole report instead.
- **Categories** group on the name with surrounding spaces removed, ignoring case. The name shown is the
  most common spelling in the group.
- **Available record count** counts records, not units.
- **Money** is `decimal` throughout. `price × quantity` is summed exactly and rounded once, to 2 places,
  half away from zero.
- **Most recently updated** is the latest `lastUpdated` instant; ties go to the lowest id. A timestamp
  without a time zone is read as UTC and counted in `summary.assumedUtcTimestamps`.
- **Duplicates.** A record repeated with identical content counts once. The same id with different
  content is fetched again and, if it persists, fails the report; the service does not guess which
  version is right.
- **No partial results.** If completeness cannot be proven, the caller gets an error, never figures from
  part of the data.
- **Ordering.** Categories by name; records within a category by id.
- **No caching or database.** Every report reads the upstream fresh, so a change upstream shows up on the
  next request.
- **Status codes** say whose fault a failure is and whether retrying helps (table above).

## Upstream behaviour

Recorded with `tools/probe` before any code was written. Details in
[`probe-findings.md`](openspec/changes/add-inventory-category-report/probe-findings.md).

- **Rate limit:** 5 requests per 10 seconds, then `429` with `Retry-After` in seconds.
- **Overlapping pages:** every even-numbered page repeats the previous page's last record, so it carries
  one record more than the page size. Nothing is ever missing. The service accepts extra records and
  lets the final count decide.
- **Messy categories:** names arrive padded and in mixed case (`' Toys'`, `'TOYS'`, `'toys '`).
- **157 records, ids 1001–1157,** identical across every pass and page size; 21 have quantity 0, among
  them all 7 in a `Discontinued` category.
- **Errors** are Problem Details. A missing key and a wrong key get the same `401`. Out-of-range paging
  gets `400`; past the last page gets an empty `200`.

To re-run the probe (stays under the rate limit; raw responses go to the git-ignored `recordings/`):

```bash
pwsh tools/probe/Invoke-UpstreamProbe.ps1 -DelayMs 2100
pwsh tools/probe/Get-ProbeSummary.ps1 -RunDir recordings/<run folder>
```

## Known limitations

- **Not built yet.** Everything marked *(once built)* is specified, not implemented.
- **About 10 seconds per report.** The upstream's rate limit, not the service, sets this. Concurrent
  callers share one collection, but each new report still pays it.
- **One instance.** Request sharing and the rate limiter live in memory. Several instances would each
  collect separately and compete for the same upstream limit; that would need a shared limiter or a
  cache.
- **No last-good fallback.** When the upstream is down, callers get an error, not the last successful
  report. Adding one would need storage and a way to show how old the data is.
- **Faults not seen in practice.** `500`s, timeouts and malformed bodies are handled and tested with
  synthetic faults, but never observed from the real upstream.
- **No metrics export.** Retries, throttling and failures are logged, not exported as metrics.
- **Category display name** follows the most common spelling in the data. It is deterministic, but a
  shift in the upstream's mix of spellings could change the name shown.

## Repository layout

```
openspec/                    project context and the specification change (proposal, design, specs, tasks, probe findings)
tools/probe/                 upstream probe and summary scripts (PowerShell)
tools/openspec/              drift checker used by spec:check
.github/workflows/           CI: spec validation and drift
.githooks/                   pre-commit hook running spec:check
src/, tests/                 (once built) the service and its tests
recordings/                  (git-ignored) raw upstream responses from probe runs
```
