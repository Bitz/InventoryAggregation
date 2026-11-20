# Probe findings: Inventory API

What the upstream actually does, as recorded by `tools/probe/`. Raw responses stay in the git-ignored
`recordings/` folder; this file holds the conclusions and what each one changes in the design. The
short form is `design.md` §7.

Re-run the probe whenever the upstream's behaviour is in question, summarize the new run, and diff its
`summary.md` against the last good one. Use `-DelayMs 2100` to stay under the rate limit.

## Runs

| Run | Requests | Result |
|---|---|---|
| 2026-11-20 manual | 1 | No key: `401` |
| 2026-11-20 probe 1 | 17 | Key sent intact (hash-checked on a local listener) but one character short: every request `401` |
| 2026-11-20 probe 2 (`20261120-015514`) | 42 | Key accepted. 0.9 s spacing tripped the rate limit: 18 `200`, 22 `429`, 2 `400` |
| 2026-11-20 probe 3 (`20261120-015643`) | 65 | 2.1 s spacing: 60 `200`, 5 `400` (the out-of-range edges), no `429`. Complete data |

## Authentication

- No key and a wrong key get the identical `401` Problem Details, `detail` "A valid API key is required
  in the X-Api-Key header.", no `WWW-Authenticate`. Our `upstream-misconfigured` message therefore says
  "missing or rejected".
- `401` responses were not rate-limited (17 in about 15 s), so authentication runs before the limiter.
- Errors are RFC 9457 Problem Details from ASP.NET Core with a `traceId`. Worth logging on our side for
  correlation; never passed to our callers verbatim.

## Rate limit

- 5 requests per 10 s, fixed window. The `429` body says so: "Rate limit of 5 requests per 10 seconds
  exceeded."
- `Retry-After` is in whole seconds and counts down to the end of the window (6, 5, 4, 3, 2, 1 on
  consecutive requests). After it reaches 0, the next 5 requests succeed.
- The `429` body has `title`, `status`, `detail`, `traceId` but no `type`.
- **Consequence:** a 157-record collection is 8 requests (7 pages + the end check), so it cannot finish
  inside one window. Without client-side pacing, every report would be throttled by design. Added
  **D22** (shared rate limiter) and the settings `RateLimitPermits` / `RateLimitWindow`. A healthy
  report now takes just over 10 s, and two reports at once share the same permits.

## Paging and completeness

- `totalRecords` 157 and `totalPages` `ceil(157 / pageSize)` on every page of every pass. Default page
  size is 20 (8 pages), as documented.
- **Every even-numbered page repeats the last record of the page before it as its first record**, byte
  for byte. Page 2 at size 25 is ids 1025–1050 (26 records). Seen at page sizes 25, 20 and 7, on all
  five passes, including a short final page (page 8 at size 20 holds 18, not 17). Nothing is ever
  missing: every pass yields exactly ids 1001–1157.
- The same page returns the same records on every pass. No conflicting duplicates.
- Past the end, and far past it (page 12 of 7), returns `200` with an empty `records` array and the
  requested `page` echoed.
- **Consequence:** the original spec failed any page with more records than `pageSize`, and a refetch
  gets the same answer, so every report would have failed. Added **D21**: only a page with *fewer*
  records than expected is a fault; extras are judged at reconciliation, where identical repeats
  collapse (D9) and the distinct count must still equal 157.

## Validation errors

| Request | Status | `detail` |
|---|---|---|
| `page=0`, `page=-1`, `page=abc` | 400 | "page must be an integer of 1 or greater." |
| `pageSize=0`, `pageSize=26` | 400 | "pageSize must be an integer from 1 to 25." |
| `pageSize=1` | 200 | 1 record, `totalPages` 157 |

## Record quality

- 157 records, ids 1001–1157, contiguous.
- **Categories are padded and in mixed case**: six spellings per category (for example `'  toys '`,
  `' Toys'`, `'toys'`, `'Toys'`, `'TOYS'`, `'Toys '`). Trimmed and case-insensitive (D11) they form 6
  groups. Among available records the most frequent trimmed spelling is the conventional one in each
  group: Electronics 13 of 25, Home & Kitchen 13 of 27, Office Supplies 15 of 30, Outdoor 13 of 27,
  Toys 14 of 27.
- **`Discontinued`**: 7 records (1151–1157), all quantity 0, so by D12 the category does not appear.
- 21 zero-quantity records in all; 136 available records.
- Prices are JSON numbers with at most 2 decimal places; quantities are integers; no nulls, missing
  fields, blank names, negatives or extra fields.
- Every `lastUpdated` ends in `Z`. No two records share a `lastUpdated`, so the tie-break (lowest id)
  never fires on today's data.

## Faults not seen

No `500`, `502`, `503`, timeouts, transport errors, truncated or non-JSON bodies, wrong `page` echoes,
drifting totals, short pages or conflicting records in 125 requests. The specs keep handling all of
them, and tests cover them with synthetic faults.

## Decisions affected

| Decision | Effect |
|---|---|
| D4 concurrency | Pacing sets the total time; concurrency only fills the first window faster |
| D9 duplicates | Supported: every repeat is identical |
| D11 category names | Supported: the rule produces the conventional names |
| D12 zero quantity | Supported: `Discontinued` drops out |
| D14 offsets | Not exercised: every timestamp is `Z`, so `assumedUtcTimestamps` is 0 today. Owner chose to read a missing offset as UTC (2026-11-20) |
| D17 caching, D24 coalescing | Each report costs about 10 s of shared rate-limit budget; owner chose to share one in-flight collection between concurrent callers, with no cache (D24, 2026-11-20) |
| D21, D22 | New, forced by the data |

## Verifying the service later

An expected report computed from the recorded data under the current rules is saved next to the raw
responses as `recordings/expected-report.json` (git-ignored, like all upstream data). Compare the
running service's output with it once the service is built.
