using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Reporting;
using InventoryAggregation.Core.Tests.Support;

namespace InventoryAggregation.Core.Tests.Reporting;

public sealed class ReportRefresherTests
{
    private static readonly DateTimeOffset T0 = RefreshHarness.Start;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "report-refresh/Scheduled refresh")]
    public async Task When_the_interval_passes_a_new_report_replaces_the_stored_one()
    {
        var source = new ScriptedPageSource(new UpstreamData().Healthy());
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        var first = harness.Store.Snapshot.Latest!;

        var after = await harness.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(RefreshTrigger.Startup, first.Trigger);
        Assert.Equal(T0, first.AsOf);
        Assert.Equal(RefreshTrigger.Scheduled, after.Latest!.Trigger);
        Assert.Equal(T0.AddMinutes(5), after.Latest.AsOf);
        Assert.Equal(16, source.Calls.Count);
        Assert.Equal(T0.AddMinutes(10), harness.Refresher.Status().NextRefreshAt);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Refresh fails after a good one")]
    public async Task A_failed_refresh_leaves_the_stored_report_and_records_the_attempt()
    {
        var data = new UpstreamData();
        var failing = false;
        var source = new ScriptedPageSource(call =>
        {
            var page = data.Page(call.Page, call.PageSize, overlap: false);
            if (Volatile.Read(ref failing) && call.Page == 3)
            {
                // Page 3 repeats page 2's last record in place of its own last one, on every pass.
                page = page with { Records = [data.Records[49], .. page.Records.Take(24)] };
            }

            return ScriptedPageSource.Ok(page);
        });
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        var stored = harness.Store.Snapshot.Latest;
        Volatile.Write(ref failing, true);

        var after = await harness.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Same(stored, after.Latest);
        Assert.True(after.IsStale);
        Assert.Equal(FailureKinds.IncompleteDataset, after.LastAttempt!.Outcome);
        Assert.Equal(T0.AddMinutes(5), after.LastAttempt.StartedAt);
        Assert.Equal(RefreshTrigger.Scheduled, after.LastAttempt.Trigger);
        Assert.Contains("157", after.LastAttempt.Detail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Rejected records under the fail policy")]
    public async Task A_rejected_record_under_the_fail_policy_leaves_the_stored_report()
    {
        var data = new UpstreamData();
        await using var harness = new RefreshHarness(
            new ScriptedPageSource(data.Healthy()),
            new InventoryReportSettings { InvalidRecordPolicy = InvalidRecordPolicy.Fail });
        await harness.StartAsync();
        var stored = harness.Store.Snapshot.Latest;
        Assert.NotNull(stored);
        data.Records[98] = data.Records[98] with { Price = -4m };

        var after = await harness.AdvanceAsync(TimeSpan.FromMinutes(5));

        Assert.Same(stored, after.Latest);
        Assert.Equal(FailureKinds.InvalidRecords, after.LastAttempt!.Outcome);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Upstream down for a while")]
    public async Task Failed_refreshes_are_retried_after_30_then_60_then_120_seconds()
    {
        var source = new ScriptedPageSource(call => ScriptedPageSource.Fail(FailureKinds.UpstreamUnavailable, call.Page));
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        var attempts = new List<DateTimeOffset> { harness.Store.Snapshot.LastAttempt!.StartedAt };

        foreach (var gap in new[] { 30, 60, 120 })
        {
            await harness.AdvanceAsync(TimeSpan.FromSeconds(gap - 1));
            Assert.Equal(attempts[^1], harness.Store.Snapshot.LastAttempt!.StartedAt);
            attempts.Add((await harness.AdvanceAsync(TimeSpan.FromSeconds(1))).LastAttempt!.StartedAt);
        }

        Assert.Equal([30d, 60d, 120d], attempts.Zip(attempts.Skip(1), (a, b) => (b - a).TotalSeconds));
        Assert.All(source.Calls, page => Assert.Equal(1, page));
        Assert.Null(harness.Store.Snapshot.Latest);
    }

    [Fact]
    public async Task The_retry_delay_stops_doubling_at_the_refresh_interval()
    {
        await using var harness = new RefreshHarness(new ScriptedPageSource(call => ScriptedPageSource.Fail(FailureKinds.UpstreamUnavailable, call.Page)));
        await harness.StartAsync();
        var gaps = new List<double>();

        for (var i = 0; i < 6; i++)
        {
            var before = harness.Store.Snapshot.LastAttempt!.StartedAt;
            var after = await harness.NextRefreshAsync();
            gaps.Add((after.LastAttempt!.StartedAt - before).TotalSeconds);
        }

        Assert.Equal([30d, 60d, 120d, 240d, 300d, 300d], gaps);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Recovery")]
    public async Task After_a_success_the_next_refresh_is_one_interval_later()
    {
        var failing = true;
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => Volatile.Read(ref failing)
            ? ScriptedPageSource.Fail(FailureKinds.UpstreamUnavailable, call.Page)
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize, overlap: true)));
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        await harness.NextRefreshAsync();
        Volatile.Write(ref failing, false);

        var recovered = await harness.NextRefreshAsync();

        Assert.Equal(T0.AddSeconds(90), recovered.LastAttempt!.StartedAt);
        Assert.True(recovered.LastAttempt.Succeeded);
        Assert.Equal(T0.AddSeconds(90), recovered.Latest!.AsOf);
        Assert.Equal(T0.AddSeconds(90).AddMinutes(5), harness.Refresher.Status().NextRefreshAt);
        var next = await harness.NextRefreshAsync();
        Assert.Equal(T0.AddSeconds(90).AddMinutes(5), next.LastAttempt!.StartedAt);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Forced refresh during a scheduled one")]
    public async Task A_forced_refresh_joins_the_scheduled_collection_in_flight()
    {
        var gate = new Gate();
        var data = new UpstreamData();
        var source = new ScriptedPageSource(async (call, token) =>
        {
            if (call.Page == 2)
            {
                await gate.PassAsync(token);
            }

            return ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize, overlap: true));
        });
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        gate.Close();

        await harness.ParkThenAdvanceAsync(TimeSpan.FromMinutes(5));
        await RefreshHarness.Until(() => source.CallsFor(2) == 2, "the scheduled collection to be fetching pages");
        var forced = harness.Refresher.GetFreshAsync(Ct);
        await RefreshHarness.Until(() => !forced.IsCompleted && harness.Refresher.Status().IsRefreshing, "the forced caller to be waiting");
        gate.Open();
        var status = await forced;

        Assert.Equal(16, source.Calls.Count);
        Assert.Equal(RefreshTrigger.Scheduled, status.Store.Latest!.Trigger);
        Assert.Equal(T0.AddMinutes(5), status.Store.Latest.AsOf);
        Assert.Same(harness.Store.Snapshot.Latest, status.Store.Latest);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Schedule fires during a forced refresh")]
    public async Task A_refresh_that_falls_due_during_a_forced_one_joins_it()
    {
        var gate = new Gate();
        var data = new UpstreamData();
        var source = new ScriptedPageSource(async (call, token) =>
        {
            if (call.Page == 2)
            {
                await gate.PassAsync(token);
            }

            return ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize, overlap: true));
        });
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        await harness.ParkThenAdvanceAsync(TimeSpan.FromSeconds(290));
        gate.Close();

        var forced = harness.Refresher.GetFreshAsync(Ct);
        await RefreshHarness.Until(() => source.CallsFor(2) == 2, "the forced collection to be fetching pages");
        await harness.ParkThenAdvanceAsync(TimeSpan.FromSeconds(10));
        await RefreshHarness.Until(() => harness.Refresher.Status().NextRefreshAt == T0.AddMinutes(10), "the schedule to take up the due refresh");
        gate.Open();
        var status = await forced;
        await RefreshHarness.Until(() => harness.Idle, "the collection to end");

        Assert.Equal(16, source.Calls.Count);
        Assert.Equal(RefreshTrigger.Fresh, status.Store.Latest!.Trigger);
        Assert.Equal(T0.AddMinutes(10), harness.Refresher.Status().NextRefreshAt);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Stored report is recent")]
    public async Task A_forced_request_within_fresh_max_age_gets_the_stored_report()
    {
        var source = new ScriptedPageSource(new UpstreamData().Healthy());
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        var stored = harness.Store.Snapshot.Latest;
        await harness.ParkThenAdvanceAsync(TimeSpan.FromSeconds(10));

        var status = await harness.Refresher.GetFreshAsync(Ct);

        Assert.Same(stored, status.Store.Latest);
        Assert.Equal(8, source.Calls.Count);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Stored report is older")]
    public async Task A_forced_request_after_fresh_max_age_runs_a_collection_that_everyone_then_gets()
    {
        var source = new ScriptedPageSource(new UpstreamData().Healthy());
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        await harness.ParkThenAdvanceAsync(TimeSpan.FromMinutes(2));

        var status = await harness.Refresher.GetFreshAsync(Ct);

        Assert.Equal(16, source.Calls.Count);
        Assert.Equal(RefreshTrigger.Fresh, status.Store.Latest!.Trigger);
        Assert.Equal(T0.AddMinutes(2), status.Store.Latest.AsOf);
        Assert.Same(status.Store.Latest, harness.Refresher.Status().Store.Latest);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Schedule unchanged")]
    public async Task A_forced_collection_does_not_move_the_schedule()
    {
        var source = new ScriptedPageSource(new UpstreamData().Healthy());
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
        await harness.ParkThenAdvanceAsync(TimeSpan.FromMinutes(2));

        var forced = await harness.Refresher.GetFreshAsync(Ct);

        Assert.Equal(RefreshTrigger.Fresh, forced.Store.Latest!.Trigger);
        Assert.Equal(T0.AddMinutes(10), forced.NextRefreshAt);
        var next = await harness.AdvanceAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(RefreshTrigger.Scheduled, next.Latest!.Trigger);
        Assert.Equal(T0.AddMinutes(10), next.Latest.AsOf);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Forced collection fails")]
    public async Task A_failed_forced_collection_returns_the_stored_report_marked_stale()
    {
        var failing = false;
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => Volatile.Read(ref failing)
            ? ScriptedPageSource.Fail(FailureKinds.UpstreamUnavailable, call.Page)
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize, overlap: true)));
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        var stored = harness.Store.Snapshot.Latest;
        Volatile.Write(ref failing, true);
        await harness.ParkThenAdvanceAsync(TimeSpan.FromMinutes(2));

        var status = await harness.Refresher.GetFreshAsync(Ct);

        Assert.Same(stored, status.Store.Latest);
        Assert.True(status.Store.IsStale);
        Assert.Equal(FailureKinds.UpstreamUnavailable, status.Store.LastAttempt!.Outcome);
        Assert.Equal(RefreshTrigger.Fresh, status.Store.LastAttempt.Trigger);
        Assert.Equal(T0.AddMinutes(5), status.NextRefreshAt);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Forced caller leaves")]
    public async Task When_the_only_forced_caller_leaves_the_collection_continues_and_is_stored()
    {
        var gate = new Gate();
        var cancelled = 0;
        var data = new UpstreamData();
        var source = new ScriptedPageSource(async (call, token) =>
        {
            if (call.Page == 2)
            {
                try
                {
                    await gate.PassAsync(token);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    throw;
                }
            }

            return ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize, overlap: true));
        });
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        gate.Close();
        await harness.ParkThenAdvanceAsync(TimeSpan.FromMinutes(2));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var forced = harness.Refresher.GetFreshAsync(caller.Token);
        await RefreshHarness.Until(() => source.CallsFor(2) == 2, "the forced collection to be fetching pages");
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => forced);
        gate.Open();
        await RefreshHarness.Until(() => harness.Store.Snapshot.Latest?.Trigger == RefreshTrigger.Fresh, "the collection to store its report");

        Assert.Equal(0, Volatile.Read(ref cancelled));
        Assert.Equal(T0.AddMinutes(2), harness.Store.Snapshot.Latest!.AsOf);
    }

    [Fact]
    public async Task A_forced_success_during_backoff_returns_the_schedule_to_the_interval()
    {
        var failing = true;
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => Volatile.Read(ref failing)
            ? ScriptedPageSource.Fail(FailureKinds.UpstreamUnavailable, call.Page)
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize, overlap: true)));
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();
        Assert.Equal(T0.AddSeconds(30), harness.Refresher.Status().NextRefreshAt);
        Volatile.Write(ref failing, false);
        await harness.ParkThenAdvanceAsync(TimeSpan.FromSeconds(10));

        var status = await harness.Refresher.GetFreshAsync(Ct);

        Assert.Equal(RefreshTrigger.Fresh, status.Store.Latest!.Trigger);
        Assert.Equal(T0.AddSeconds(10).AddMinutes(5), status.NextRefreshAt);
        var calls = source.Calls.Count;
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromMilliseconds(20), Ct);
        Assert.Equal(calls, source.Calls.Count);
        var next = await harness.AdvanceAsync(TimeSpan.FromMinutes(4.5));
        Assert.Equal(RefreshTrigger.Scheduled, next.LastAttempt!.Trigger);
        Assert.Equal(T0.AddSeconds(10).AddMinutes(5), next.LastAttempt.StartedAt);
    }

    [Fact]
    public async Task A_bug_in_a_refresh_is_recorded_as_internal_error_and_the_schedule_carries_on()
    {
        var throwing = true;
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => Volatile.Read(ref throwing)
            ? throw new InvalidOperationException("bug")
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize, overlap: true)));
        await using var harness = new RefreshHarness(source);
        await harness.StartAsync();

        Assert.Equal(FailureKinds.InternalError, harness.Store.Snapshot.LastAttempt!.Outcome);
        Assert.Equal(T0.AddSeconds(30), harness.Refresher.Status().NextRefreshAt);
        Volatile.Write(ref throwing, false);
        var next = await harness.NextRefreshAsync();
        Assert.True(next.LastAttempt!.Succeeded);
    }

    /// <summary>A pass that pages wait on while it is closed.</summary>
    private sealed class Gate
    {
        private volatile TaskCompletionSource _open = Opened();

        public void Close() => _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _open.TrySetResult();

        public Task PassAsync(CancellationToken cancellationToken) => _open.Task.WaitAsync(cancellationToken);

        private static TaskCompletionSource Opened()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
