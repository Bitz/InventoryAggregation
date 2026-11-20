## ADDED Requirements

### Requirement: Settings are bound from one section and validated at startup
The service SHALL bind `InventoryApiOptions` from the `InventoryApi` configuration section and validate
it with `ValidateOnStart`. Startup SHALL fail, naming the setting, when any rule in `design.md` §8 is
broken.

#### Scenario: Missing key
- **WHEN** the app starts with no `InventoryApi:ApiKey`
- **THEN** startup fails with a message naming `InventoryApi:ApiKey` and how to set it

#### Scenario: Page size out of range
- **WHEN** `InventoryApi:PageSize` is 50
- **THEN** startup fails with a message naming `InventoryApi:PageSize` and the range 1–25

### Requirement: The API key never enters source control
No committed file SHALL contain an API key. `appsettings.json` SHALL hold `BaseUrl` (when it is not
secret) and tuning defaults only. The key SHALL be read from .NET user secrets in `Development` and from
the environment variable `InventoryApi__ApiKey` elsewhere. `.gitignore` SHALL exclude local secret and
`.env` files.

#### Scenario: Clean clone
- **WHEN** the repository is searched for the key value
- **THEN** no file in any commit contains it

#### Scenario: Environment variable
- **WHEN** `InventoryApi__ApiKey` is set and no user secret exists
- **THEN** the service uses the environment variable's value

### Requirement: The key never enters logs or responses
The API key SHALL NOT appear in any log entry, Problem Details body, exception message surfaced to a
caller, or options dump. `IHttpClientFactory` header logging SHALL redact the key header.

#### Scenario: Debug logging on
- **WHEN** the log level is `Debug` and a report runs
- **THEN** no log entry contains the key value

### Requirement: The service's own components are registered through dependency injection
The service SHALL register its own components (`InventoryApiClient` as `IInventoryPageSource`,
`InventoryCollector`, `RecordValidator`, `CategoryAggregator`, `InventoryReportService`, `TimeProvider`)
through one extension method, `AddInventoryAggregation(IConfiguration)`. No component SHALL construct
another with `new` except value objects.

#### Scenario: Swapping the source in tests
- **WHEN** endpoint tests replace `IInventoryPageSource` with a scripted fake
- **THEN** every other component is the production registration
