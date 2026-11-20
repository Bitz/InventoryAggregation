## MODIFIED Requirements

### Requirement: An audit of the run is available on request
`GET /api/inventory/categories?include=audit` SHALL add an `audit` block to the success response for the
run that produced the served report: its id, start and end, the dataset fingerprint and three sections,
`extract`, `transform` and `load`. Without `include=audit` the response SHALL NOT contain `audit`
(`design.md` D26). A failed refresh SHALL NOT replace the served audit.

#### Scenario: Audit requested
- **WHEN** a caller requests the report with `include=audit`
- **THEN** the response holds `audit` with `runId`, `startedAt`, `finishedAt`, `fingerprint`, `extract`, `transform` and `load`

#### Scenario: Audit not requested
- **WHEN** a caller requests the report without `include`
- **THEN** the response has no `audit` property

#### Scenario: Audit of a stale report
- **WHEN** the latest refresh failed and an older good report is served with `include=audit`
- **THEN** the audit's `runId` and `finishedAt` are those of the run that produced the served report
