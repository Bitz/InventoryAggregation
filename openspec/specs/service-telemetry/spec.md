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

### Requirement: A report is one trace
Each report request SHALL produce one trace: the server span for `GET /api/inventory/categories`, a child
span named `collect inventory` for the shared collection with its outcome, pages and requests as attributes,
and the upstream requests beneath it.

#### Scenario: Healthy report
- **WHEN** a report succeeds over 7 pages in 8 requests
- **THEN** a `collect inventory` span is recorded with `inventory.outcome` `success`, `inventory.pages` 7 and `inventory.requests` 8

#### Scenario: Failed report
- **WHEN** a report fails with `incomplete-dataset`
- **THEN** the `collect inventory` span has status error and `inventory.outcome` `incomplete-dataset`

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
The service SHALL count collections by outcome (`success` or the failure kind), record their duration, and
count upstream retries, page refetches and re-collections, on a meter named `InventoryAggregation`.

#### Scenario: Outcomes counted
- **WHEN** one report succeeds and one fails with `upstream-unavailable`
- **THEN** `inventory.collections` records one `success` and one `upstream-unavailable`

#### Scenario: Retries counted
- **WHEN** a page is retried twice
- **THEN** `inventory.upstream.retries` records 2
