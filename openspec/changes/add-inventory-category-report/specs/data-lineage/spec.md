## ADDED Requirements

### Requirement: An audit of the run is available on request
`GET /api/inventory/categories?include=audit` SHALL add an `audit` block to the success response with the
run's id, start and end, the dataset fingerprint and three sections: `extract`, `transform` and `load`.
Without `include=audit` the response SHALL NOT contain `audit` (`design.md` D26).

#### Scenario: Audit requested
- **WHEN** a caller requests the report with `include=audit`
- **THEN** the response holds `audit` with `runId`, `startedAt`, `finishedAt`, `fingerprint`, `extract`, `transform` and `load`

#### Scenario: Audit not requested
- **WHEN** a caller requests the report without `include`
- **THEN** the response has no `audit` property

### Requirement: The extract section accounts for what was received
`extract` SHALL report the upstream's totals, the records received counting repeats, the distinct records,
the requests, every page refetch with its reason, every re-collection with its reason, and every collapsed
duplicate with the pages it arrived on.

#### Scenario: Overlapping pages
- **WHEN** even pages repeat the previous page's last record
- **THEN** each repeated id is listed with both pages, and `recordsReceived` minus the repeats equals `distinctRecords`

#### Scenario: Refetch reasons
- **WHEN** page 3 is short once and whole on refetch
- **THEN** `pageRefetches` lists page 3 with the check that failed

### Requirement: The transform section lists every correction
`transform` SHALL list, per category, each raw spelling with its record count and the name it became; the ids
whose timestamps were converted from an offset and those read as UTC; and, per category, the ids excluded
for zero quantity, including categories that drop out entirely.

#### Scenario: Category spellings
- **WHEN** records carry `"  toys "` four times and `"Toys"` ten times
- **THEN** the `Toys` entry lists both spellings with counts 4 and 10

#### Scenario: Timestamp corrections
- **WHEN** one record has `+02:00` and another has no offset
- **THEN** the first id is under `convertedToUtc` and the second under `assumedUtc`

#### Scenario: A category that drops out
- **WHEN** every `Discontinued` record has quantity 0
- **THEN** `zeroQuantityExcluded` lists `Discontinued` with its ids, and `categories` has no `Discontinued` entry

### Requirement: The load section adds up
`load` SHALL give, per category and in total, the records in, the records excluded by reason and the records
out, so that records in equals excluded plus out, and the totals equal the summary's.

#### Scenario: Counts reconcile
- **WHEN** an audit is produced
- **THEN** for each category `recordsIn = zeroQuantityExcluded + recordsOut`, and the totals plus `rejected` equal `summary.totalRecords`

### Requirement: The dataset has a fingerprint
The audit SHALL carry `fingerprint`, a SHA-256 over the distinct records exactly as received, ordered by id,
so that two runs over the same data give the same value and any change in the data gives another.

#### Scenario: Same data
- **WHEN** the same upstream data is collected twice
- **THEN** both audits carry the same fingerprint

#### Scenario: Changed data
- **WHEN** one record's price differs between two runs
- **THEN** the fingerprints differ

### Requirement: Every run leaves a manifest
For every collection the service SHALL write one `Information` log record, the run manifest, whose
structured properties carry the run id, trace id, outcome, fingerprint and the extract, transform and load
counts. It SHALL also count rejected records by reason, collapsed duplicates and records whose category
spelling was normalized, on the `InventoryAggregation` meter.

#### Scenario: Manifest for a run
- **WHEN** a report succeeds or fails
- **THEN** exactly one manifest record is logged for that run, with its outcome and counts

#### Scenario: Data-quality counters
- **WHEN** a run rejects one record for a negative price and collapses 3 duplicates
- **THEN** `inventory.records.rejected` records 1 with reason `price is negative` and `inventory.records.duplicates_collapsed` records 3
