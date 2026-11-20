# Design: Name the last refresh's time `startedAt`

## 1. What changes

`freshness.lastRefresh` keeps its three members and their meaning; only the name of the time changes:

```json
"lastRefresh": { "startedAt": "2026-11-20T15:03:55Z", "outcome": "success" }
```

The same object appears in `503 report-not-ready` bodies, renamed the same way.

## 2. Compatibility

The rename is breaking. It is done outright because the freshness block is new and has no known consumers.

## 3. Alternatives

- **Keep `at` and document it as the start time.** The name would still invite the wrong reading.
- **Report the finish time instead.** It would sit next to `asOf` and say little more; the start time is what
  ties an attempt to the schedule (`nextRefreshAt` is counted from scheduled starts).

## 4. Decisions

Earlier decisions (archived with `add-inventory-category-report` and `serve-last-good-report`, up to D36) stand
unless revised below.

| # | Decision | Why | Status |
|---|---|---|---|
| D18 | The response follows the expected shape and adds `summary` and `freshness`. | Unchanged. | Confirmed 2026-11-20 |
| D34 | The success response adds `freshness` and the `Age` header. | Revised by D37: `lastRefresh.at` is now `lastRefresh.startedAt`. | Revised 2026-11-20 |
| D37 | The latest attempt's time is named `startedAt`. | It is the attempt's start, about 10 s before `asOf`; `at` read as a clock error. | Confirmed 2026-11-20 |

## 5. Failure kinds

Unchanged; see the archived `serve-last-good-report` design, §5.
