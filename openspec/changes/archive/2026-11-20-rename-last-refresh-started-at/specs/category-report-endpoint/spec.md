## MODIFIED Requirements

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

### Requirement: Every report says whether refreshing is failing
`freshness` SHALL carry `stale` (true when the latest attempt failed after the served report was made),
`overdue` (true when the report is older than `StaleAfter`), and `lastRefresh` with the latest attempt's
`startedAt` (when it started), its `outcome` (`success` or a failure kind from `design.md` §5) and, when it
failed, its `detail` (`design.md` D37).

#### Scenario: Healthy refresh
- **WHEN** the latest refresh succeeded
- **THEN** `stale` and `overdue` are false and `lastRefresh.outcome` is `success`

#### Scenario: Overdue report
- **WHEN** no refresh has succeeded for longer than `StaleAfter`
- **THEN** the response is `200` with `overdue` true
