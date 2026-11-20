# Tasks: Serve the last good report

Read `proposal.md` and `design.md` first. Nothing under `InventoryReportService` (collector, validator,
aggregator, upstream client, rate limiter) should change; if it has to, stop and say why.

## 1. Settings (`service-configuration`)
- [x] `RefreshInterval`, `RefreshRetryDelay`, `StaleAfter`, `FreshMaxAge` on `InventoryApiOptions`, with the
      rules in `design.md` §8 in `InventoryApiOptionsValidator`; defaults in `appsettings.json`.
- [x] Startup failure for out-of-order refresh settings names both settings.

## 2. Store and refresher (`report-refresh`)
- [x] `ReportStore` in Core: the latest good `CategoryReport` with its trigger, plus the last attempt's time,
      outcome and detail. Thread-safe; replaced only on success (D7).
- [x] `ReportRefresher` in Core: one collection at a time (move the sharing out of `InventoryReportService`,
      which becomes a plain collect ▸ validate ▸ aggregate call); `RefreshAsync(trigger)`;
      `GetFreshAsync(cancellationToken)` with the `FreshMaxAge` floor (D31).
- [x] Collections run to the end or `CollectionTimeout` and are cancelled only by shutdown (D33). Drop the
      waiter counting. A caller's token only stops that caller's wait (`Task.WaitAsync`).
- [x] Schedule: due times from the previous scheduled start (D30, D31); retry delay doubling from
      `RefreshRetryDelay` up to `RefreshInterval` after failures (D32). All timing on `TimeProvider`.
- [x] `ReportRefreshService : BackgroundService` in Api: starts the first refresh without blocking startup,
      runs the schedule, stops on shutdown without recording a failure.

## 3. Endpoint (`category-report-endpoint`)
- [x] Plain request reads the store; `fresh=true` calls `GetFreshAsync`. Bind `fresh` as `bool?`.
- [x] `freshness` block and `Age` header (D34) on every `200`.
- [x] `503` `report-not-ready` with `Retry-After` and `lastRefresh` when nothing is stored (D29). Add
      `report-not-ready` to `FailureKinds` and the one status map; remove the collection-kind ▸ status
      mapping from the endpoint path.
- [x] Caller disconnect during `fresh=true`: stop waiting, no error logged, collection continues.
- [x] OpenAPI: `fresh` and `include` parameters; responses `200`, `400`, `500`, `503`; description updated.

## 4. Lineage and telemetry (`data-lineage`, `service-telemetry`)
- [x] `include=audit` returns the stored run's audit.
- [x] Root span `refresh inventory report` with `inventory.trigger`; `collect inventory` beneath it; link
      from a waiting `fresh=true` request span (D35).
- [x] `inventory.collections` tagged with trigger; `inventory.report.age` observable gauge.
- [x] `Warning` when a refresh fails and the previous report is kept.

## 5. Tests
- [x] Every scenario in this change's specs has a test with
      `[Trait("Scenario", "<capability>/<title>")]`. Move or delete tests for the three removed
      `category-report-endpoint` requirements and the removed telemetry requirement.
- [x] Endpoint tests per collection failure kind: `200` stale after a good report, `503` with none.
- [x] Refresher tests on `FakeTimeProvider`: schedule, backoff, floor, sharing, schedule not moved by a
      forced refresh, shutdown. No test waits on real time.
- [x] Fake upstream: a way to switch the fault mode while running (or a mode that fails after N good
      collections) so the stale path can be shown by hand.

## 6. Documentation
- [x] README: the endpoint section (freshness block, `fresh=true`, `Age`, the one `503`), status table,
      assumptions (D27–D36 in plain words), and limitations: in memory only (restart gap), each instance
      refreshes on its own, figures up to `RefreshInterval` old when healthy. Replace the "Built for
      on-demand use on one instance" and "No caching, no persistence, no last-good fallback" items.

## 7. Verify
- [x] `dotnet build` with 0 warnings, `dotnet test`, `npm run spec:check`.
- [x] Run against the fake upstream: healthy, then a failing mode; show `stale` true with the old figures,
      then recovery. Run with a wrong key from start; show `503 report-not-ready`.
- [x] Run once against the real upstream; compare the figures with `recordings/expected-report.json`.
- [x] Archive the change (`openspec archive serve-last-good-report`) once everything above passes, and
      confirm `spec:check` reports no pending scenarios.
