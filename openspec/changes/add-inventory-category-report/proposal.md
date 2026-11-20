# Change: Add the inventory category report

**Status: proposed 2026-11-20.** Nothing is built.

## Why

Stock reporting needs per-category figures (inventory value, available record count, most recent
change, and the records behind them). The only source is the Inventory API, which pages its results
and is not reliable: it fails some requests, and a successful response is not guaranteed to be a
correct page. Reading page one and summing it, or trusting every `200`, gives figures that look right
and are not. The service has to own the job of getting a complete, consistent dataset before it
computes anything.

## What changes

- **inventory-source**: a typed upstream client that fetches one page, retries transient failures
  with bounded backoff, fails fast on the rest, and returns a classified result instead of throwing.
- **inventory-collection**: walks every page, checks each page against the first, reconciles the
  collected set against the reported total, confirms the end of the data, and refetches when a static
  dataset gives two different answers. Fails with a named reason when it cannot prove completeness.
- **inventory-records**: validates and normalizes each record (category spelling, timestamps, money)
  without knowing HTTP exists.
- **category-aggregation**: drops zero-quantity records, groups by category, computes the figures and
  carries the records they came from. Pure and deterministic.
- **category-report-endpoint**: `GET /api/inventory/categories` returns the report, or a Problem
  Details response that says which upstream failure stopped it. No unhandled exception reaches a caller.
- **service-configuration**: base URL, API key and tuning knobs bound from configuration, validated at
  startup; the key is kept out of source control and logs.

## Impact

- New repository; no existing behaviour changes.
- One outbound dependency: the Inventory API. Its key is a secret held in user secrets locally and an
  environment variable elsewhere.
- Upstream load per report request: `ceil(totalRecords / 25) + 1` requests when healthy, more on
  retries and refetches, bounded by `MaxConcurrency` and `CollectionTimeout`.

## Decisions

The judgement calls (zero-quantity categories, ties, rounding, category spelling, invalid records,
partial results, status codes) are D1–D18 in `design.md` §4.
