# Change: Serve the last good report

**Status: proposed 2026-11-20.** Nothing is built.

## Why

The report is built live on every request. The upstream allows 5 requests per 10 s and a report needs 8,
so every caller waits about 10 s, and whenever the upstream misbehaves the caller gets an error: the
service will not show figures it cannot prove complete (D7). Consumers of the report would rather not
receive errors. A report that was proven complete a few minutes ago is a better answer than an error,
as long as the response says how old it is.

## What changes

- **report-refresh** (new): a background refresh builds the report at startup and every 5 minutes, keeps
  the latest good one in memory, keeps it when a refresh fails, and retries failed refreshes sooner.
  `fresh=true` lets a caller ask for a live report. One collection runs at a time, whoever asked for it.
- **category-report-endpoint**: serves the stored report instantly with a `freshness` block and an `Age`
  header. A failed refresh no longer reaches the caller as an error. The only remaining error is `503`
  with `kind: report-not-ready` while no good report has ever been produced. Callers that leave stop
  waiting but no longer cancel the collection.
- **service-telemetry**: each refresh is its own trace; the stored report's age is measured.
- **data-lineage**: `include=audit` returns the audit of the run that produced the served report.
- **service-configuration**: four new settings and the new components in the registration.

## Impact

- Responses no longer wait for the upstream (except `fresh=true`), and upstream load no longer depends on
  traffic: 8 requests per refresh, one refresh per 5 minutes per instance.
- Figures can be up to 5 minutes old when healthy, and older when the upstream is failing; the response
  always says so.
- Callers that relied on a `5xx` to detect upstream trouble must read `freshness.stale` instead.
- A restart leaves no report for about 10 s; requests in that time get `report-not-ready`.

## Decisions

D7, D16, D17, D18, D23 and D24 are revised; D27–D36 are new. All in `design.md` §4.
