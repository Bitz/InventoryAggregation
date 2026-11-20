## ADDED Requirements

### Requirement: Each record is validated before it can feed a figure
`RecordValidator` SHALL accept a raw record only if: `id` is present and positive; `name` is present and
not blank; `category` is present and not blank after trimming; `price` is present and not negative;
`quantity` is present, an integer and not negative; `lastUpdated` is present and parses as ISO 8601
with or without an offset (`design.md` D14). A record failing any rule SHALL be rejected with every reason that
applies, not just the first.

#### Scenario: Several problems
- **WHEN** a record has a blank category and `quantity: -2`
- **THEN** it is rejected with two reasons: blank category, negative quantity

#### Scenario: Timestamp without offset
- **WHEN** `lastUpdated` is `2026-10-15T12:30:00`
- **THEN** the record is accepted with `2026-10-15T12:30:00Z` and counted as an assumed-UTC timestamp

#### Scenario: Timestamp that does not parse
- **WHEN** `lastUpdated` is `yesterday`
- **THEN** the record is rejected for an unparseable timestamp

#### Scenario: Zero quantity is valid
- **WHEN** a record has `quantity: 0` and every other field valid
- **THEN** the record is accepted (aggregation drops it later)

### Requirement: Rejected records follow the configured policy
With `InvalidRecordPolicy = Report` (default) the service SHALL exclude rejected records from every
figure and list each in the response with its `id` (when present) and reasons. With
`InvalidRecordPolicy = Fail`, any rejected record SHALL fail the report with `invalid-records`
listing them.

#### Scenario: Report policy
- **WHEN** 1 of 157 records is rejected under `Report`
- **THEN** figures are computed from the other 156 and the response lists the rejected record

#### Scenario: Fail policy
- **WHEN** 1 of 157 records is rejected under `Fail`
- **THEN** the report fails with `invalid-records` and returns no figures

### Requirement: Accepted records are normalized
An accepted record SHALL have its category trimmed, its `lastUpdated` converted to a UTC
`DateTimeOffset`, and its price held as `decimal` read directly from the JSON number without passing
through a binary floating-point type. Category grouping keys SHALL be compared ordinally ignoring case
(`design.md` D11).

#### Scenario: Offset converted
- **WHEN** `lastUpdated` is `2026-10-15T14:30:00+02:00`
- **THEN** the accepted record holds `2026-10-15T12:30:00Z`

#### Scenario: Exact money
- **WHEN** `price` is `0.1`
- **THEN** the accepted price equals `0.1m` exactly

### Requirement: Validation knows nothing about transport
`RecordValidator` SHALL live in Core, take raw records as input and return accepted and rejected sets.
It SHALL have no dependency on HTTP, JSON serializers or configuration.

#### Scenario: Tested without a network
- **WHEN** the validator's tests run
- **THEN** they construct raw records in memory and use no HTTP or JSON types
