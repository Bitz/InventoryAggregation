# Change: Name the last refresh's time `startedAt`

**Status: proposed 2026-11-20.**

## Why

`freshness.lastRefresh.at` is the time the latest refresh attempt *started*. A refresh takes about 10 s, so
`lastRefresh.at` reads about 10 s earlier than `asOf`, the time the figures were made. Named `at`, that looks
like a clock error. Naming it `startedAt` says what it is.

## What changes

- **category-report-endpoint**: `freshness.lastRefresh.at` becomes `freshness.lastRefresh.startedAt`, in the
  `200` response and in the `lastRefresh` of a `503 report-not-ready`. The value is unchanged.

## Impact

- Breaking for any caller that reads `lastRefresh.at`. The field shipped in the same release cycle as the
  freshness block and has no known consumers yet, so it is renamed outright rather than sent under both names.

## Decisions

D34 is revised by D37. See `design.md` §4.
