## ADDED Requirements

### Requirement: Every report response links to the refresh that made it
Every `200` SHALL link its server span to the `refresh inventory report` span of the refresh that produced
the served report, also when that report is stale, and SHALL carry that link once (`design.md` D38).

#### Scenario: Plain request
- **WHEN** a request without `fresh` is answered `200`
- **THEN** its server span has exactly one link, to the `refresh inventory report` span of the refresh that produced the served report

#### Scenario: Stale report
- **WHEN** the latest refresh failed and a request is answered with the older report
- **THEN** its server span links to the refresh that produced that report, not to the failed one

#### Scenario: Forced request with a new report
- **WHEN** a request with `fresh=true` waits on a collection that succeeds
- **THEN** its server span has exactly one link, to that collection's `refresh inventory report` span
