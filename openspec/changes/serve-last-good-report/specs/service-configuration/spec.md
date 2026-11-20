## MODIFIED Requirements

### Requirement: Settings are bound from one section and validated at startup
The service SHALL bind `InventoryApiOptions` from the `InventoryApi` configuration section, including the
refresh settings (`design.md` D36), and validate it with `ValidateOnStart`. Startup SHALL fail, naming the
setting, when any rule in `design.md` §8 is broken.

#### Scenario: Missing key
- **WHEN** the app starts with no `InventoryApi:ApiKey`
- **THEN** startup fails with a message naming `InventoryApi:ApiKey` and how to set it

#### Scenario: Page size out of range
- **WHEN** `InventoryApi:PageSize` is 50
- **THEN** startup fails with a message naming `InventoryApi:PageSize` and the range 1–25

#### Scenario: Refresh settings out of order
- **WHEN** `InventoryApi:StaleAfter` is shorter than `InventoryApi:RefreshInterval`
- **THEN** startup fails with a message naming both settings

### Requirement: The service's own components are registered through dependency injection
The service SHALL register its own components (`InventoryApiClient` as `IInventoryPageSource`,
`InventoryCollector`, `RecordValidator`, `CategoryAggregator`, `InventoryReportService`, `ReportStore`,
`ReportRefresher`, the hosted `ReportRefreshService`, `TimeProvider`) through one extension method,
`AddInventoryAggregation(IConfiguration)`. No component SHALL construct another with `new` except value
objects.

#### Scenario: Swapping the source in tests
- **WHEN** endpoint tests replace `IInventoryPageSource` with a scripted fake
- **THEN** every other component is the production registration
