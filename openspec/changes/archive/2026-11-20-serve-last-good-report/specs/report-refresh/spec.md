## ADDED Requirements

### Requirement: The report is refreshed in the background
The service SHALL start a refresh when it starts, without delaying startup, and SHALL refresh again every
`RefreshInterval` measured from the previous scheduled start (`design.md` D27, D30). A refresh SHALL run
the existing collect ▸ validate ▸ aggregate pipeline unchanged.

#### Scenario: Startup refresh
- **WHEN** the service starts with a healthy upstream
- **THEN** a refresh starts immediately, the host accepts requests before it finishes, and a good report is stored when it ends

#### Scenario: Scheduled refresh
- **WHEN** a good report was stored and `RefreshInterval` passes on the clock
- **THEN** a new refresh runs and its report replaces the stored one

### Requirement: Only a good report is stored
A refresh SHALL replace the stored report only when it produces a report. A failed refresh SHALL leave the
stored report as it was and record its time and failure kind as the last attempt (`design.md` D7). Partial
figures SHALL never be stored or served.

#### Scenario: Refresh fails after a good one
- **WHEN** a good report is stored and the next refresh fails with `incomplete-dataset`
- **THEN** the stored report is unchanged and the last attempt records `incomplete-dataset` and its time

#### Scenario: Rejected records under the fail policy
- **WHEN** `InvalidRecordPolicy` is `Fail` and a refresh finds a rejected record
- **THEN** the stored report is unchanged and the last attempt records `invalid-records`

### Requirement: A failed refresh is retried sooner
After a failed refresh the next attempt SHALL be due after `RefreshRetryDelay`, doubling after each further
failure up to `RefreshInterval`. After a success the schedule SHALL return to `RefreshInterval`
(`design.md` D32).

#### Scenario: Upstream down for a while
- **WHEN** refreshes fail three times in a row with `RefreshRetryDelay` 30 s and `RefreshInterval` 5 min
- **THEN** the attempts are 30 s, 60 s and 120 s apart

#### Scenario: Recovery
- **WHEN** a refresh succeeds after failures
- **THEN** the next refresh is due `RefreshInterval` later

### Requirement: One collection runs at a time
At most one collection SHALL run at a time. A startup, scheduled or forced refresh that finds a collection
in flight SHALL wait for its outcome instead of starting another (`design.md` D24).

#### Scenario: Forced refresh during a scheduled one
- **WHEN** a caller asks for `fresh=true` while a scheduled refresh is collecting
- **THEN** the upstream receives one collection's requests and the caller gets that collection's report

#### Scenario: Schedule fires during a forced refresh
- **WHEN** the scheduled refresh falls due while a forced collection is running
- **THEN** no second collection starts

### Requirement: A caller can ask for a live report
With `fresh=true` the service SHALL serve the stored report as is when it is younger than `FreshMaxAge`.
Otherwise the caller SHALL wait for a collection, joining one in flight or starting one. A successful
forced collection SHALL replace the stored report for every caller and SHALL NOT move the schedule
(`design.md` D31).

#### Scenario: Stored report is recent
- **WHEN** a caller asks for `fresh=true` 10 s after a good refresh, with `FreshMaxAge` 30 s
- **THEN** the stored report is returned and no upstream request is made

#### Scenario: Stored report is older
- **WHEN** a caller asks for `fresh=true` 2 minutes after a good refresh
- **THEN** a collection runs, the caller gets its report with `trigger` `fresh`, and later plain requests get the same report

#### Scenario: Schedule unchanged
- **WHEN** a forced collection succeeds 2 minutes after a scheduled refresh started
- **THEN** the next scheduled refresh is still due 5 minutes after that scheduled start

#### Scenario: Forced collection fails
- **WHEN** a caller asks for `fresh=true` and the collection fails with `upstream-unavailable` while a good report is stored
- **THEN** the caller gets the stored report with `stale` true and `lastRefresh.outcome` `upstream-unavailable`

### Requirement: A collection finishes whoever is waiting
A collection, once started, SHALL run until it ends or `CollectionTimeout` passes, whether or not anyone is
waiting for it. Application shutdown SHALL cancel it, and a cancelled collection SHALL NOT be recorded as
a failed attempt (`design.md` D33).

#### Scenario: Forced caller leaves
- **WHEN** the only caller waiting on a forced collection disconnects
- **THEN** the collection continues and its report is stored

#### Scenario: Shutdown
- **WHEN** the service stops while a refresh is collecting
- **THEN** the in-flight upstream requests are cancelled and no failure is recorded
