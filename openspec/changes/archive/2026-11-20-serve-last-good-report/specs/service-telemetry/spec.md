## MODIFIED Requirements

### Requirement: Report outcomes and retries are measured
The service SHALL count collections by outcome (`success` or the failure kind) and by trigger (`startup`,
`scheduled` or `fresh`), record their duration, count upstream retries, page refetches and re-collections,
and report the stored report's age in seconds as `inventory.report.age`, on a meter named
`InventoryAggregation`.

#### Scenario: Outcomes counted
- **WHEN** one refresh succeeds and one fails with `upstream-unavailable`
- **THEN** `inventory.collections` records one `success` and one `upstream-unavailable`, each with its trigger

#### Scenario: Retries counted
- **WHEN** a page is retried twice
- **THEN** `inventory.upstream.retries` records 2

#### Scenario: Report age
- **WHEN** the stored report was made 90 s ago
- **THEN** `inventory.report.age` reads 90

## ADDED Requirements

### Requirement: A refresh is one trace
Each refresh SHALL produce one trace: a root span `refresh inventory report` with `inventory.trigger`, a
child span `collect inventory` with its outcome, pages and requests as attributes, and the upstream
requests beneath it. A request with `fresh=true` that waits on a collection SHALL link to that trace
(`design.md` D35). A failed refresh that leaves an older report in place SHALL be logged at `Warning`.

#### Scenario: Healthy refresh
- **WHEN** a scheduled refresh succeeds over 7 pages in 8 requests
- **THEN** a `refresh inventory report` span with `inventory.trigger` `scheduled` holds a `collect inventory` span with `inventory.outcome` `success`, `inventory.pages` 7 and `inventory.requests` 8

#### Scenario: Failed refresh
- **WHEN** a refresh fails with `incomplete-dataset` while a good report is stored
- **THEN** the `collect inventory` span has status error and `inventory.outcome` `incomplete-dataset`, and a `Warning` says the previous report is kept

#### Scenario: Forced request
- **WHEN** a request with `fresh=true` waits on a collection
- **THEN** its server span has a link to that collection's `refresh inventory report` span

## REMOVED Requirements

### Requirement: A report is one trace
**Reason**: Collections no longer run inside a request (`design.md` D27).
**Migration**: "A refresh is one trace".
