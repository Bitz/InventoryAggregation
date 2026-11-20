# Design: Link every report response to the refresh that made it

## 1. What changes

The stored report remembers the span context of the refresh that produced it. The endpoint adds a link from the
request's server span to that context on every `200`, unless the request already links to it because it
waited on that collection with `fresh=true`.

| Request | Links on its server span |
|---|---|
| plain, report stored | the refresh that produced the served report |
| `fresh=true`, served from the store (younger than `FreshMaxAge`) | the refresh that produced the served report |
| `fresh=true`, waited on a collection that succeeded | that collection's refresh (one link) |
| `fresh=true`, waited on a collection that failed | that collection's refresh, and the refresh that produced the stale report served |
| `503 report-not-ready` | none |

## 2. Where it lives

The span context is a value carried on `StoredReport`, set by `ReportRefresher` when it stores a report. Core
already uses `System.Diagnostics` for its spans, so no new reference. The endpoint adds the link; it is part of
mapping the stored report to the response.

## 3. Alternatives

- **Make each request a child of the refresh trace.** A request is its own trace with its own parent (the
  caller's); a link is the OpenTelemetry way to point at work done elsewhere.
- **Put the refresh's trace id in the response body.** `audit.runId` already ties the body to the run, and the
  run manifest log carries the trace id; the link covers the trace backend without changing the contract.

## 4. Decisions

Earlier decisions (archived up to D37) stand unless revised below.

| # | Decision | Why | Status |
|---|---|---|---|
| D35 | Each refresh is its own trace; a request that waits on a collection links to it. | Revised by D38: every `200` also links to the refresh that produced the served report. | Revised 2026-11-20 |
| D38 | Every `200` links its server span to the `refresh inventory report` span of the refresh that produced the served report, once. | A caller's trace should lead to the run behind its figures, stale or not, without reading logs. | Confirmed 2026-11-20 |
