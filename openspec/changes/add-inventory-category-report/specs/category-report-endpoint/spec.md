## ADDED Requirements

### Requirement: One GET endpoint serves the category report
The service SHALL expose `GET /api/inventory/categories`. On success it SHALL return `200` with
`application/json` in camelCase:

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
    "availableRecords": 140,
    "zeroQuantityExcluded": 16,
    "rejectedRecords": [ { "id": 1099, "reasons": ["lastUpdated has no offset"] } ],
    "pagesFetched": 7,
    "requests": 8,
    "generatedAt": "2026-11-20T15:04:05Z"
  }
}
```

`summary` is an addition to the expected shape (`design.md` D18). Timestamps SHALL be ISO 8601 UTC with
`Z`. Money SHALL be a JSON number with at most 2 decimal places.

#### Scenario: Healthy upstream
- **WHEN** a caller requests the report and collection succeeds
- **THEN** the response is `200` with one entry per category and a summary whose counts add up:
  `availableRecords + zeroQuantityExcluded + rejectedRecords.length = totalRecords`

### Requirement: Failures are answered with Problem Details, never with partial figures
When the report cannot be produced the endpoint SHALL return an RFC 9457 Problem Details body
(`application/problem+json`) with `type`, `title`, `status`, `detail`, a `kind` extension from
`design.md` §5 and the request's `traceId`. Status codes SHALL follow `design.md` D16. A `503` caused by
throttling SHALL carry a `Retry-After` header. The body SHALL NOT contain figures, the API key, the
upstream base URL's credentials, or a stack trace.

#### Scenario: Upstream down
- **WHEN** every attempt for page 1 fails with a connection error
- **THEN** the response is `502` with `kind: upstream-unavailable`

#### Scenario: Upstream throttling
- **WHEN** the upstream answers `429` with `Retry-After: 120`
- **THEN** the response is `503` with `kind: upstream-throttled` and `Retry-After: 120`

#### Scenario: Wrong key
- **WHEN** the upstream answers `401`
- **THEN** the response is `500` with `kind: upstream-misconfigured` and the detail does not contain the key

#### Scenario: Data does not reconcile
- **WHEN** collection fails with `incomplete-dataset`
- **THEN** the response is `502`, the detail gives expected and collected counts, and no categories are returned

#### Scenario: Deadline
- **WHEN** collection exceeds `CollectionTimeout`
- **THEN** the response is `504` with `kind: upstream-timeout`

### Requirement: No unhandled exception reaches a caller
The host SHALL register a global exception handler that turns any unhandled exception into a `500`
Problem Details response with `kind: internal-error` and the `traceId`, and logs the exception. Exception
messages and stack traces SHALL appear in the response only in the `Development` environment.

#### Scenario: A bug in the service
- **WHEN** the report service throws an unexpected exception
- **THEN** the caller receives `500` Problem Details with a `traceId` and no stack trace in Production

### Requirement: A caller that leaves stops the work
The endpoint SHALL pass `HttpContext.RequestAborted` to the report service so that upstream requests stop
when the caller disconnects. A cancelled request SHALL NOT be logged as an error.

#### Scenario: Caller disconnects mid-collection
- **WHEN** the caller closes the connection while pages are being fetched
- **THEN** in-flight upstream requests are cancelled and no error is logged

### Requirement: The endpoint holds no business logic
The endpoint SHALL call `InventoryReportService`, map its success to the response contract and its
failure to Problem Details, and do nothing else. Mapping from failure kind to status SHALL live in one
place.

#### Scenario: Endpoint tests with a fake service
- **WHEN** the endpoint tests run with `WebApplicationFactory` and a fake page source
- **THEN** each failure kind in `design.md` §5 produces the status D16 assigns to it
