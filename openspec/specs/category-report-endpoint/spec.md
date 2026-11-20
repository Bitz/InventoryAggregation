# category-report-endpoint Specification

## Purpose

Serve the latest good category report at `GET /api/inventory/categories` with how old it is and whether refreshing is failing, a live one on request, and `503 report-not-ready` only while no good report exists. Never partial figures.

## Requirements

### Requirement: One GET endpoint serves the category report
The service SHALL expose `GET /api/inventory/categories`. It SHALL answer from the stored report without
contacting the upstream, unless the caller asks for `fresh=true` (`report-refresh`). On success it SHALL
return `200` with `application/json` in camelCase:

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
  },
  "freshness": {
    "asOf": "2026-11-20T15:04:05Z",
    "ageSeconds": 42,
    "trigger": "scheduled",
    "stale": false,
    "overdue": false,
    "lastRefresh": { "startedAt": "2026-11-20T15:03:55Z", "outcome": "success" },
    "nextRefreshAt": "2026-11-20T15:08:55Z"
  }
}
```

`summary` and `freshness` are additions to the expected shape (`design.md` D18, D34). Each entry in
`rejectedRecords` has the record's `id` (when present) and its `reasons`. Timestamps SHALL be ISO 8601 UTC
with `Z`.

#### Scenario: Healthy upstream
- **WHEN** a good report is stored and a caller requests the report
- **THEN** the response is `200` with one entry per category and a summary whose counts add up:
  `availableRecords + zeroQuantityExcluded + rejectedRecords.length = totalRecords`

#### Scenario: No upstream request
- **WHEN** a good report is stored and a caller requests the report without `fresh`
- **THEN** the upstream receives no request

### Requirement: Money in the response is exact
Computed money (`totalInventoryValue`) SHALL be a JSON number with at most 2 decimal places. An item's
`price` SHALL be echoed exactly as the upstream sent it, so that the items still add up to the figure
(`design.md` D13).

#### Scenario: Price with more than 2 places
- **WHEN** one record has `price: 0.005` and `quantity: 1`, and another `price: 149.99` and `quantity: 12`
- **THEN** the item prices are `0.005` and `149.99` and `totalInventoryValue` is `1799.89`

### Requirement: No unhandled exception reaches a caller
The host SHALL register a global exception handler that turns any unhandled exception into a `500`
Problem Details response with `kind: internal-error` and the `traceId`, and logs the exception. Exception
messages and stack traces SHALL appear in the response only in the `Development` environment.

#### Scenario: A bug in the service
- **WHEN** the report service throws an unexpected exception
- **THEN** the caller receives `500` Problem Details with a `traceId` and no stack trace in Production

### Requirement: The endpoint holds no business logic
The endpoint SHALL read the stored report, or ask `ReportRefresher` for a live one when `fresh=true`, map
it to the response contract, and do nothing else. The endpoint's statuses SHALL be `200`, `400` for an
unreadable query, `500` with `kind: internal-error` and `503` with `kind: report-not-ready`
(`design.md` D16).

#### Scenario: Endpoint tests with a fake service
- **WHEN** the endpoint tests run with `WebApplicationFactory` and a fake page source, once for each collection failure kind in `design.md` §5
- **THEN** a request after a good report and then that failure gets `200` with `stale` true and that kind, and a request with no good report gets `503` with `kind: report-not-ready`

### Requirement: The endpoint is described by an OpenAPI document in Development
In the `Development` environment the service SHALL serve the built-in ASP.NET Core OpenAPI document at
`/openapi/v1.json`, describing `GET /api/inventory/categories` with its `fresh` and `include` parameters,
its `200` response and its Problem Details responses (`400`, `500`, `503`), and Swagger UI at `/swagger`
rendering that document. Outside `Development` neither SHALL be served (`design.md` D19).

#### Scenario: Development
- **WHEN** the service runs in `Development` and `/openapi/v1.json` and `/swagger/index.html` are requested
- **THEN** the document is `200` and lists the report route, its parameters and its response statuses
- **AND** the Swagger UI page is `200` and points at `/openapi/v1.json`

#### Scenario: Production
- **WHEN** the service runs in `Production` and `/openapi/v1.json` or `/swagger/index.html` is requested
- **THEN** the response is `404`

### Requirement: Every report says how old it is
Every `200` SHALL carry `freshness` with `asOf` (equal to `summary.generatedAt`), `ageSeconds`, `trigger`
(`startup`, `scheduled` or `fresh`) and `nextRefreshAt`, and an `Age` header with `ageSeconds`
(`design.md` D34).

#### Scenario: Fresh report
- **WHEN** the latest refresh succeeded 42 s ago
- **THEN** `ageSeconds` is 42 and the `Age` header is `42`

### Requirement: Every report says whether refreshing is failing
`freshness` SHALL carry `stale` (true when the latest attempt failed after the served report was made),
`overdue` (true when the report is older than `StaleAfter`), and `lastRefresh` with the latest attempt's
`startedAt` (when it started), its `outcome` (`success` or a failure kind from `design.md` §5) and, when it
failed, its `detail` (`design.md` D37). `startedAt` SHALL be in whole seconds, truncated
(`design.md` D39).

#### Scenario: Healthy refresh
- **WHEN** the latest refresh succeeded
- **THEN** `stale` and `overdue` are false and `lastRefresh.outcome` is `success`

#### Scenario: Overdue report
- **WHEN** no refresh has succeeded for longer than `StaleAfter`
- **THEN** the response is `200` with `overdue` true

#### Scenario: Start in whole seconds
- **WHEN** the latest attempt started at 15:04:06.7
- **THEN** `lastRefresh.startedAt` is `2026-11-20T15:04:06Z`, in the `200` response and in a `503` body

### Requirement: A failed refresh does not reach the caller as an error
While a good report is stored, a failed refresh SHALL NOT change the response status: the caller SHALL get
`200` with the stored report, `stale` true and the failure in `lastRefresh` (`design.md` D7, D16). The
detail SHALL NOT contain the API key, the upstream address or a stack trace.

#### Scenario: Upstream down after a good report
- **WHEN** a good report is stored and the latest refresh failed with `upstream-unavailable`
- **THEN** the response is `200` with the stored figures, `stale` true and `lastRefresh.outcome` `upstream-unavailable`

#### Scenario: Wrong key after a good report
- **WHEN** a good report is stored and the latest refresh failed with `upstream-misconfigured`
- **THEN** the response is `200` with `stale` true and the detail does not contain the key

### Requirement: No report yet is answered with 503
When no good report has been produced since startup, the endpoint SHALL return `503` Problem Details
(`application/problem+json`) with `type` `urn:inventory-aggregation:report-not-ready`, `title`, `status`,
`detail`, `kind: report-not-ready`, the request's `traceId`, and `lastRefresh` (time and outcome) when an
attempt has finished (`design.md` D29). The body SHALL NOT contain figures, the API key, the upstream
address or a stack trace.

#### Scenario: Wrong key at startup
- **WHEN** every refresh since startup failed with `upstream-misconfigured`
- **THEN** the response is `503` with `kind: report-not-ready`, `lastRefresh.outcome` `upstream-misconfigured`, and no key in the body

#### Scenario: Forced report with nothing stored
- **WHEN** a caller asks for `fresh=true`, nothing is stored and the collection fails
- **THEN** the response is `503` with `kind: report-not-ready` and that failure in `lastRefresh`

### Requirement: The 503 says when to retry
A `report-not-ready` response SHALL carry `Retry-After` in whole seconds: `RateLimitWindow` while a refresh
is running, otherwise the seconds until the next attempt is due plus `RateLimitWindow`.

#### Scenario: Request during the startup refresh
- **WHEN** a caller requests the report while the first refresh is still collecting
- **THEN** the response is `503` with `kind: report-not-ready` and `Retry-After: 10`

#### Scenario: Between failed attempts
- **WHEN** the last refresh failed and the next attempt is due in 25 s
- **THEN** `Retry-After` is `35`

### Requirement: A caller that leaves stops waiting
When a caller waiting on `fresh=true` disconnects, the endpoint SHALL stop waiting for it, SHALL NOT cancel
the collection (`design.md` D33), and SHALL NOT log an error.

#### Scenario: Caller disconnects during a forced collection
- **WHEN** the caller closes the connection while a forced collection is fetching pages
- **THEN** the collection carries on and no error is logged
