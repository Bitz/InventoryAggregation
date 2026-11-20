# inventory-collection Specification

## Purpose

Walk every page the upstream reports and prove the collected dataset complete and consistent before any figure is computed: check each page against the first, refetch pages that disagree, confirm the end of the data, reconcile against the reported total, and fail with a named reason when completeness cannot be proven.

## Requirements

### Requirement: The collector walks every page the upstream reports
`InventoryCollector` SHALL depend only on `IInventoryPageSource`. It SHALL fetch page 1 with the
configured `PageSize`, take that page's `totalPages` and `totalRecords` as the reference, then fetch
pages 2 to `totalPages` with no more than `MaxConcurrency` requests in flight. When `totalRecords` is 0
the dataset SHALL be empty and valid, with `totalPages` of 0 or 1 accepted.

#### Scenario: 157 records, page size 25
- **WHEN** page 1 reports 157 records over 7 pages
- **THEN** pages 1 to 7 are each fetched and 157 records are collected

#### Scenario: Concurrency bound
- **WHEN** `MaxConcurrency` is 4 and there are 40 pages
- **THEN** no more than 4 page requests are outstanding at any moment

#### Scenario: Empty dataset
- **WHEN** page 1 reports 0 records, 0 pages and an empty array
- **THEN** collection succeeds with no records

### Requirement: Every page is checked against the reference
For each page the collector SHALL check that:
`page` equals the page requested; `pageSize` equals the size requested; `totalPages` and `totalRecords`
equal the reference; and `totalPages` equals `ceil(totalRecords / pageSize)`.

#### Scenario: Wrong page returned
- **WHEN** a request for page 4 returns a body saying `page: 3`
- **THEN** the page fails the check for `page`

### Requirement: A page holds no fewer records than expected
A page SHALL hold at least `pageSize` records, or `totalRecords - (totalPages - 1) × pageSize` on the
last page. A page holding more SHALL pass; whether the extra records are harmless is settled by
reconciliation (`design.md` D21).

#### Scenario: Overlapping page
- **WHEN** page 2 of 7 holds 26 records and its first record repeats the last record of page 1
- **THEN** the page passes, and reconciliation collapses the repeated record

#### Scenario: Short page
- **WHEN** page 3 of 7 holds 24 records
- **THEN** the page fails this check

### Requirement: A page that disagrees is fetched again
A page that fails a check, or came back `malformed-response`, SHALL be fetched again up to
`PageRefetchLimit` (default 2) more times. If it still fails, collection SHALL fail with
`inconsistent-paging` (or `malformed-response`) naming the page and the check that failed.

#### Scenario: Totals drift
- **WHEN** page 5 reports `totalRecords: 160` against a reference of 157, on every refetch
- **THEN** collection fails with `inconsistent-paging` naming page 5, the field, and both values

#### Scenario: Short page in the middle
- **WHEN** page 3 of 7 returns 24 records once and 25 on refetch
- **THEN** the refetched page is used and collection continues

### Requirement: The end of the data is confirmed
After the last reported page, the collector SHALL request page `totalPages + 1`. A `200` with no records,
or `upstream-misconfigured` from a `400`, SHALL confirm the end. Any records on that page SHALL fail
collection with `inconsistent-paging` stating that the totals understate the data.

#### Scenario: Totals understate the data
- **WHEN** totals say 7 pages and page 8 returns 3 records
- **THEN** collection fails with `inconsistent-paging` for page 8

#### Scenario: Past-the-end returns 400
- **WHEN** page 8 of 7 returns `400`
- **THEN** the end is confirmed and collection continues to reconciliation

### Requirement: Duplicate ids are collapsed or flagged
The collector SHALL collapse records that share an `id` and have identical content into one, and SHALL
flag ids that appear with different content as conflicts (`design.md` D9).

#### Scenario: Identical duplicate
- **WHEN** id 1020 appears on pages 1 and 2 with identical content
- **THEN** one record with id 1020 is kept and 1020 is listed among collapsed duplicates

### Requirement: The collected set reconciles with the reported total
The count of distinct ids SHALL equal `totalRecords`, with no conflicts. Otherwise the collector SHALL
repeat the whole collection once (`CollectionAttempts` default 2, `design.md` D20), and if that also fails, fail with
`incomplete-dataset` or `conflicting-records` carrying expected count, collected count, duplicate ids
and conflicting ids.

#### Scenario: Page boundary overlap
- **WHEN** page 3 repeats the last record of page 2 and omits one record, on both attempts
- **THEN** collection fails with `incomplete-dataset`, expected 157, collected 156, duplicate id listed

#### Scenario: A one-off glitch
- **WHEN** the first attempt is short by one record and the second attempt reconciles
- **THEN** collection succeeds with the second attempt's records

#### Scenario: Same id, different content
- **WHEN** id 1042 appears on two pages with different `quantity`, on both attempts
- **THEN** collection fails with `conflicting-records` listing 1042

### Requirement: Collection is bounded in size and time
Collection SHALL fail with `dataset-too-large` when page 1 reports more than `MaxPages` (default 1000)
pages, before fetching any other page. The whole collection, including re-collection, SHALL be
cancelled after `CollectionTimeout` (default 60 s) and fail with `upstream-timeout`. Cancellation from
the caller SHALL stop all in-flight page requests.

#### Scenario: Absurd totals
- **WHEN** page 1 reports 5,000,000 pages
- **THEN** collection fails with `dataset-too-large` after one request

#### Scenario: Caller goes away
- **WHEN** the caller's token is cancelled with pages in flight
- **THEN** every in-flight request is cancelled and no new request starts

### Requirement: A page-source failure ends collection
A failure returned by the page source for any page (other than the past-the-end `400`) SHALL end
collection with that failure's kind, after the source's own retries. Pages already collected SHALL be
discarded; no partial dataset is returned (`design.md` D7).

#### Scenario: One page never succeeds
- **WHEN** page 6 of 7 fails with `upstream-unavailable`
- **THEN** collection fails with `upstream-unavailable` naming page 6 and returns no records

### Requirement: The outcome reports the checks that ran
A successful collection SHALL report: records, `totalRecords`, pages fetched, total requests, page
refetches, collection attempts and duplicate ids collapsed. A failed collection SHALL report its kind
and the details its requirement names.

#### Scenario: Healthy run
- **WHEN** 157 records are collected over 7 pages with no faults
- **THEN** the outcome reports 8 requests (7 pages plus the end check), 0 refetches and 1 attempt
