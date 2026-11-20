# Change: Link every report response to the refresh that made it

**Status: proposed 2026-11-20.**

## Why

Since reports are built in the background, a plain request's trace holds only the server span: nothing leads
from "this caller got these figures" to the collection that produced them. Only a `fresh=true` request that
waited on a collection links to it. When figures look wrong, the first question is which run made them, and
the answer should be one click away in the trace backend.

## What changes

- **service-telemetry** (new requirement): every `200` links its server span to the `refresh inventory report` span of the
  refresh that produced the served report, including a stale one. A `fresh=true` request that waited on a
  collection keeps its link to that collection; when the two are the same refresh there is one link.

## Impact

- One link per response; no new spans, no new requests, nothing new leaves the process.
- `503` responses have no report and so no link.

## Decisions

D35 is revised by D38. See `design.md` §4.
