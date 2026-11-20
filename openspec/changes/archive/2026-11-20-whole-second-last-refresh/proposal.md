# Change: Give the last refresh's start in whole seconds

**Status: proposed 2026-11-20.**

## Why

`freshness.lastRefresh.startedAt` carries the clock's full precision (`2026-11-20T17:33:23.8120461Z`). The
start of a refresh matters to the second at most, the `Age` header and `ageSeconds` are whole seconds, and the
spec's examples show whole seconds. Seven fractional digits are noise to a reader.

## What changes

- **category-report-endpoint**: `lastRefresh.startedAt` is given in whole seconds, truncated, in the `200`
  response and in the `503 report-not-ready` body. The other timestamps are unchanged.

## Impact

- `startedAt` can read up to a second earlier than the attempt really started. Nothing is computed from it.

## Decisions

D39 is new. See `design.md` §4.
