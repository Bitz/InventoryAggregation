# category-aggregation Specification

## Purpose

Turn accepted records into per-category figures: drop zero-quantity records, group by category, and compute total value, available record count, most recent record and the items behind them. Pure and deterministic.

## Requirements

### Requirement: Zero-quantity records contribute to nothing
`CategoryAggregator` SHALL drop accepted records with `quantity` 0 before grouping. They SHALL NOT count
toward any category's value, record count, most recent record or items. A category whose records all
have `quantity` 0 SHALL NOT appear in the result (`design.md` D12). The number dropped SHALL be
returned for the summary.

#### Scenario: Mixed category
- **WHEN** Electronics has 3 records, one with `quantity` 0
- **THEN** Electronics reports 2 available records and its items list those 2

#### Scenario: All out of stock
- **WHEN** every Garden record has `quantity` 0
- **THEN** no Garden entry appears and the dropped count includes them

### Requirement: Records group into one result per category
The aggregator SHALL group the remaining records by category key (`inventory-records`), producing one
result per key. The displayed category name SHALL be the most frequent trimmed spelling in the group,
ties broken by ordinal order (`design.md` D11).

#### Scenario: Spelling variants
- **WHEN** records carry `Electronics` twice and `electronics` once
- **THEN** one category named `Electronics` holds all three records

### Requirement: Category figures are exact
For each category the aggregator SHALL compute:
- `totalInventoryValue` as the sum of `price × quantity` in `decimal`, rounded once to 2 places with
  `MidpointRounding.AwayFromZero` (`design.md` D13);
- `availableRecordCount` as the number of records in the category (records, not units);
- `mostRecentlyUpdated` as the record with the latest `lastUpdated` instant, ties to the lowest `id`
  (`design.md` D14);
- `items` as exactly the records the other figures were computed from.

#### Scenario: Value
- **WHEN** a category holds 149.99 × 12 and 0.005 × 1
- **THEN** `totalInventoryValue` is 1799.89 (1799.885 rounded away from zero)

#### Scenario: Tie on lastUpdated
- **WHEN** ids 1007 and 1003 share the latest `lastUpdated`
- **THEN** `mostRecentlyUpdated` is 1003

#### Scenario: Items match figures
- **WHEN** a category reports `availableRecordCount` 4
- **THEN** its `items` holds those 4 records and summing their `price × quantity` gives the reported value

### Requirement: Output order is stable
Categories SHALL be ordered by display name, ordinal ignoring case; items within a category by `id`
ascending (`design.md` D15).

#### Scenario: Same input, same output
- **WHEN** the same records are aggregated twice in different input orders
- **THEN** both results are identical

### Requirement: Aggregation is pure
The aggregator SHALL be a deterministic function of its input records. It SHALL perform no I/O, read no
clock and hold no state between calls.

#### Scenario: Tested in memory
- **WHEN** the aggregator's tests run
- **THEN** they need no host, network, clock or configuration
