## MODIFIED Requirements

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
