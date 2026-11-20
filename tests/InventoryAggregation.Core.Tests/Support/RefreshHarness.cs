using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace InventoryAggregation.Core.Tests.Support;

/// <summary>
/// The report refresher wired as the host wires it, around a scripted page source and a fake clock,
/// with its schedule running. Nothing here waits on real time: the clock moves only when a test says
/// so, and <see cref="Until"/> spins without a timer.
/// </summary>
internal sealed class RefreshHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 20, 15, 4, 5, TimeSpan.Zero);

    private readonly ServiceProvider _services;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _schedule;

    public RefreshHarness(IInventoryPageSource source, InventoryReportSettings? settings = null)
    {
        Clock = new TrackedClock(Time);
        _services = Components.ReportServices(source, settings, Clock);
        Refresher = _services.GetRequiredService<ReportRefresher>();
        Store = _services.GetRequiredService<ReportStore>();
    }

    public FakeTimeProvider Time { get; } = new(Start);

    /// <summary>The clock the refresher runs on: <see cref="Time"/>, with its pending timers visible.</summary>
    public TrackedClock Clock { get; }

    public ReportRefresher Refresher { get; }

    public ReportStore Store { get; }

    public IServiceProvider Services => _services;

    public bool Idle => !Refresher.Status().IsRefreshing;

    /// <summary>Starts the schedule and waits for the startup refresh to finish.</summary>
    public async Task StartAsync()
    {
        _schedule = Refresher.RunScheduleAsync(_stopping.Token);
        await Until(() => Store.Snapshot.LastAttempt is not null && Idle, "the startup refresh to finish");
    }

    /// <summary>Starts the schedule without waiting for anything.</summary>
    public void StartSchedule() => _schedule = Refresher.RunScheduleAsync(_stopping.Token);

    /// <summary>
    /// Moves the clock by <paramref name="by"/>, first waiting until the schedule is parked on the timer
    /// for its next refresh, so that the timer is set from the right moment and fires where it should.
    /// Returns whether the move passes the next due refresh.
    /// </summary>
    public async Task<bool> ParkThenAdvanceAsync(TimeSpan by)
    {
        var next = Refresher.Status().NextRefreshAt;
        var now = Time.GetUtcNow();
        var due = next <= now + by;
        if (next > now)
        {
            await Until(() => Clock.HasTimerDueAt(next), "the schedule to wait for its next refresh");
        }

        Time.Advance(by);
        return due;
    }

    /// <summary>Moves the clock by <paramref name="by"/> and, if that makes a refresh run, waits until it has finished.</summary>
    public async Task<ReportSnapshot> AdvanceAsync(TimeSpan by)
    {
        var before = Store.Snapshot;
        if (await ParkThenAdvanceAsync(by))
        {
            await Until(() => !ReferenceEquals(Store.Snapshot, before) && Idle, "the refresh to finish");
        }

        return Store.Snapshot;
    }

    /// <summary>Moves the clock to the next due refresh and waits until it has finished.</summary>
    public Task<ReportSnapshot> NextRefreshAsync() => AdvanceAsync(Refresher.Status().NextRefreshAt - Time.GetUtcNow());

    public async Task StopAsync()
    {
        await Refresher.ShutdownAsync();
        await _stopping.CancelAsync();
        if (_schedule is not null)
        {
            await _schedule;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _stopping.Dispose();
        await _services.DisposeAsync();
    }

    /// <summary>Waits, without a timer, until a condition holds. Fails after 10 s of real time.</summary>
    public static async Task Until(Func<bool> condition, string what)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new TimeoutException($"Gave up waiting for: {what}");
            }

            await Task.Yield();
            Thread.Yield();
        }
    }
}
