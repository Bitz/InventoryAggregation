# service-telemetry Specification

## Purpose

Make the service observable: logs, traces and metrics through OpenTelemetry, exported over OTLP when configured, per-page progress in the console, and no upstream address or key in any of it.

## Requirements

### Requirement: Telemetry is exported over OTLP only when configured
The service SHALL produce logs, traces and metrics through OpenTelemetry and SHALL export them over OTLP
only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set, configured by the standard `OTEL_*` settings (protocol,
headers, service name). Without an endpoint nothing SHALL be exported and nothing SHALL be sent anywhere.
The service SHALL log at startup whether export is on, without the endpoint's headers (`design.md` D25).

#### Scenario: No endpoint configured
- **WHEN** the service starts without `OTEL_EXPORTER_OTLP_ENDPOINT`
- **THEN** it logs that telemetry export is off and registers no exporter

#### Scenario: Endpoint configured
- **WHEN** the service starts with `OTEL_EXPORTER_OTLP_ENDPOINT` set and `OTEL_EXPORTER_OTLP_HEADERS` holding a credential
- **THEN** it logs that telemetry export is on over OTLP
- **AND** the log entry contains neither the headers nor the credential

### Requirement: Telemetry carries no upstream address and no key
Spans for upstream requests SHALL keep the method, the status and the request path (with the query as the
runtime reports it, which masks it by default), and SHALL NOT carry the upstream host, port or full URL. No span, metric or log record SHALL contain the API key. The
HTTP client's own metrics, which are tagged with the upstream host, SHALL NOT be collected.

#### Scenario: Upstream request span
- **WHEN** an upstream request span ends with `url.full`, `server.address` and `server.port` set by the HTTP client
- **THEN** `url.full` holds only the path and query, and `server.address` and `server.port` are removed

### Requirement: Progress is logged per page
At `Information` level the service SHALL log a line per page fetch (page, total pages, records or failure
kind, attempts, duration), one for the end check, one per rate-limit permit wait, and a closing line with
records, pages, requests and elapsed time. Each line SHALL start with a short id of the shared collection
and SHALL NOT contain the upstream address or the key.

#### Scenario: Healthy report progress
- **WHEN** a report collects 7 pages and the end check
- **THEN** the log holds a line for each of pages 1 to 7 with `/7`, an end-check line and a closing line, all with the same collection id

#### Scenario: Waiting for a permit
- **WHEN** an upstream request has to wait 10 s for a rate-limit permit
- **THEN** a line names the page and the wait

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
