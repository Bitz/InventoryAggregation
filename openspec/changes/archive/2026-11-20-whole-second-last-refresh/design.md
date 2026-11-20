# Design: Give the last refresh's start in whole seconds

## 1. What changes

`LastRefreshResponse` truncates the attempt's start to the second when it maps it; the store keeps the full
value, so scheduling and logs are unaffected.

## 2. Truncate, not round

Truncation never moves the time past the real start, and matches `ageSeconds`, which counts whole seconds
elapsed.

## 3. Other timestamps

`asOf` (equal to `summary.generatedAt`), `nextRefreshAt` and the audit's times keep full precision. `asOf` must
equal `summary.generatedAt`, so changing one means changing both; that is left for a separate decision.

## 4. Decisions

Earlier decisions (archived up to D38) stand.

| # | Decision | Why | Status |
|---|---|---|---|
| D37 | The latest attempt's time is named `startedAt`. | Unchanged; D39 sets its precision. | Confirmed 2026-11-20 |
| D39 | `lastRefresh.startedAt` is given in whole seconds, truncated. | The start matters to the second at most; full precision is noise next to whole-second ages. | Confirmed 2026-11-20 |

## 5. Failure kinds

Unchanged; see the archived `serve-last-good-report` design, §5.
