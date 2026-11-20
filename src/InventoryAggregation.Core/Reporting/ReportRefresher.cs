using System.Diagnostics;
using InventoryAggregation.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace InventoryAggregation.Core.Reporting;

/// <summary>The stored report and the refresh state at one moment, as the endpoint shows them.</summary>
/// <param name="Store">The latest good report and the last attempt.</param>
/// <param name="IsRefreshing">A collection is running.</param>
/// <param name="NextRefreshAt">When the schedule runs the next refresh, or retries a failed one.</param>
public sealed record RefreshStatus(ReportSnapshot Store, bool IsRefreshing, DateTimeOffset NextRefreshAt);

/// <summary>
/// Decides when the report is built and shares one collection between everyone who wants it
/// (design.md D24, D27–D33). At most one collection runs at a time: a startup, scheduled or forced
/// refresh that finds one running waits for its outcome. A collection runs to its end (the collector
/// bounds it with <c>CollectionTimeout</c>) whoever is waiting; only <see cref="ShutdownAsync"/>
/// cancels it. Every outcome lands in the <see cref="ReportStore"/> before any waiter sees it.
/// All timing is on the injected <see cref="TimeProvider"/>.
/// </summary>
public sealed partial class ReportRefresher : IDisposable
{
    private readonly InventoryReportService _reports;
    private readonly ReportStore _store;
    private readonly InventoryReportSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<ReportRefresher> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _gate = new();

    private Run? _current;
    private DateTimeOffset _nextDue;
    private int _failures;
    private TaskCompletionSource _rescheduled = NewSignal();

    public ReportRefresher(
        InventoryReportService reports,
        ReportStore store,
        InventoryReportSettings settings,
        TimeProvider time,
        InventoryMetrics metrics,
        ILogger<ReportRefresher> logger)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        _reports = reports;
        _store = store;
        _settings = settings;
        _time = time;
        _logger = logger;
        _nextDue = time.GetUtcNow();
        metrics.TrackReportAge(() => _store.Snapshot.Latest is { } latest ? Math.Max(0, (_time.GetUtcNow() - latest.AsOf).TotalSeconds) : null);
    }

    public RefreshStatus Status()
    {
        lock (_gate)
        {
            return new RefreshStatus(_store.Snapshot, _current is not null, _nextDue);
        }
    }

    /// <summary>
    /// Starts a collection, or joins the one running, and waits for its outcome. The token only stops
    /// this caller's wait; the collection carries on (D33).
    /// </summary>
    public Task<ReportResult> RefreshAsync(RefreshTrigger trigger, CancellationToken cancellationToken = default) =>
        StartOrJoin(trigger, scheduledAt: null).Task.WaitAsync(cancellationToken);

    /// <summary>
    /// <c>fresh=true</c> (D31): the stored report when it is younger than <c>FreshMaxAge</c>; otherwise
    /// waits for a collection (joining one in flight) and returns the state after it. A successful
    /// forced collection replaces the stored report for everyone; a failed one leaves it marked stale.
    /// </summary>
    public async Task<RefreshStatus> GetFreshAsync(CancellationToken cancellationToken)
    {
        if (_store.Snapshot.Latest is { } latest && _time.GetUtcNow() - latest.AsOf < _settings.FreshMaxAge)
        {
            return Status();
        }

        var run = StartOrJoin(RefreshTrigger.Fresh, scheduledAt: null);

        // The caller's span (the request) points at the refresh it waited for (D35).
        if (run.Context is { } context)
        {
            Activity.Current?.AddLink(new ActivityLink(context));
        }

        try
        {
            await run.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The service is stopping; answer with what is stored.
        }

        return Status();
    }

    /// <summary>
    /// The schedule (D30–D32): a refresh at once, then one every <c>RefreshInterval</c> from the previous
    /// scheduled start. After a failure the next attempt is due after <c>RefreshRetryDelay</c>, doubling
    /// per further failure up to <c>RefreshInterval</c>; a success returns to the interval. Runs until
    /// <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    public async Task RunScheduleAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var trigger = RefreshTrigger.Startup;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await WaitUntilDueAsync(stoppingToken).ConfigureAwait(false);

                DateTimeOffset scheduled;
                lock (_gate)
                {
                    // Until this refresh says otherwise, the next one is one interval on.
                    scheduled = _nextDue;
                    _nextDue = scheduled + _settings.RefreshInterval;
                }

                // The run sets the next due time itself, before anyone sees its outcome; this covers a
                // run that had already finished when the schedule joined it.
                var run = StartOrJoin(trigger, scheduled);
                try
                {
                    await run.Task.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // A bug in the refresh: already logged and recorded as internal-error by the run.
                }

                lock (_gate)
                {
                    if (!run.ScheduleApplied)
                    {
                        ApplySchedule(scheduled, run.Succeeded);
                        run.ScheduleApplied = true;
                    }
                }

                trigger = RefreshTrigger.Scheduled;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Cancels the running collection, if any, and waits for it to end. A cancelled collection is not recorded (D33).</summary>
    public async Task ShutdownAsync()
    {
        Run? running;
        lock (_gate)
        {
            running = _current;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (running is not null)
        {
            await ((Task)running.Task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose() => _shutdown.Dispose();

    /// <param name="trigger">What asks for the refresh.</param>
    /// <param name="scheduledAt">For the schedule: the due time this refresh answers, so its outcome sets the next one.</param>
    private Run StartOrJoin(RefreshTrigger trigger, DateTimeOffset? scheduledAt)
    {
        lock (_gate)
        {
            if (_current is { } running)
            {
                var joining = trigger.Name();
                LogJoining(joining, running.TriggerName);
                if (scheduledAt is not null && !running.Finished)
                {
                    running.ScheduledAt ??= scheduledAt;
                }

                return running;
            }

            _shutdown.Token.ThrowIfCancellationRequested();
            var run = new Run(trigger, _time.GetUtcNow(), StartRefreshActivity(trigger)) { ScheduledAt = scheduledAt };
            _current = run;
            run.Task = Task.Run(() => ExecuteAsync(run));
            return run;
        }
    }

    private async Task<ReportResult> ExecuteAsync(Run run)
    {
        // The collection belongs to the refresh's trace, not to whichever request started it.
        Activity.Current = run.Activity;
        try
        {
            var previous = _store.Snapshot.Latest;
            ReportResult result;
            try
            {
                result = await _reports.BuildReportAsync(run.Trigger, _shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                LogCancelled(run.TriggerName);
                throw;
            }
            catch (Exception exception)
            {
                run.Activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
                _store.RecordFailure(
                    new InventoryFailure(FailureKinds.InternalError, "The refresh failed unexpectedly; the service log has the details."),
                    run.Trigger,
                    run.StartedAt);
                LogRefreshThrew(exception, run.TriggerName);
                Finish(run, succeeded: false);
                throw;
            }

            if (result.IsSuccess)
            {
                _store.RecordSuccess(result.Report, run.Trigger, run.StartedAt);
                var summary = result.Report.Summary;
                LogStored(run.TriggerName, summary.TotalRecords, summary.GeneratedAt);
            }
            else
            {
                run.Activity?.SetStatus(ActivityStatusCode.Error, result.Failure.Kind);
                _store.RecordFailure(result.Failure, run.Trigger, run.StartedAt);
                var failure = result.Failure;
                if (previous is not null)
                {
                    var asOf = previous.AsOf;
                    LogKeptPrevious(run.TriggerName, failure.Kind, failure.Detail, asOf);
                }
                else
                {
                    LogNoReportYet(run.TriggerName, failure.Kind, failure.Detail);
                }
            }

            Finish(run, result.IsSuccess);
            return result;
        }
        finally
        {
            run.Activity?.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_current, run))
                {
                    _current = null;
                }
            }
        }
    }

    /// <summary>Records the outcome on the run and moves the schedule, all before any waiter resumes.</summary>
    private void Finish(Run run, bool succeeded)
    {
        lock (_gate)
        {
            run.Finished = true;
            run.Succeeded = succeeded;
            if (run.ScheduledAt is { } scheduled)
            {
                ApplySchedule(scheduled, succeeded);
                run.ScheduleApplied = true;
            }
            else if (succeeded && _failures > 0)
            {
                // A forced success during backoff returns the schedule to the interval from that
                // success (D32). A forced refresh never moves a healthy schedule (D31).
                _failures = 0;
                _nextDue = run.StartedAt + _settings.RefreshInterval;
                var signal = _rescheduled;
                _rescheduled = NewSignal();
                signal.TrySetResult();
            }
        }
    }

    /// <summary>Called under the lock. <paramref name="scheduled"/> is the due time the finished refresh answered.</summary>
    private void ApplySchedule(DateTimeOffset scheduled, bool succeeded)
    {
        var now = _time.GetUtcNow();
        if (succeeded)
        {
            _failures = 0;
            _nextDue = Max(scheduled + _settings.RefreshInterval, now);
            return;
        }

        _failures++;
        _nextDue = now + Backoff(_failures);
        var seconds = (_nextDue - now).TotalSeconds;
        LogRetryDue(_failures, seconds);
    }

    private TimeSpan Backoff(int failures)
    {
        var delay = _settings.RefreshRetryDelay;
        for (var i = 1; i < failures && delay < _settings.RefreshInterval; i++)
        {
            delay += delay;
        }

        return delay < _settings.RefreshInterval ? delay : _settings.RefreshInterval;
    }

    private async Task WaitUntilDueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            DateTimeOffset due;
            Task rescheduled;
            lock (_gate)
            {
                due = _nextDue;
                rescheduled = _rescheduled.Task;
            }

            var wait = due - _time.GetUtcNow();
            if (wait <= TimeSpan.Zero)
            {
                return;
            }

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await Task.WhenAny(Task.Delay(wait, _time, timer.Token), rescheduled).ConfigureAwait(false);
            await timer.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>The root span of a refresh's own trace (D35), whatever span is current where it starts.</summary>
    private static Activity? StartRefreshActivity(RefreshTrigger trigger)
    {
        var current = Activity.Current;
        Activity.Current = null;
        try
        {
            var activity = InventoryTelemetry.ActivitySource.StartActivity("refresh inventory report", ActivityKind.Internal);
            activity?.SetTag("inventory.trigger", trigger.Name());
            return activity;
        }
        finally
        {
            Activity.Current = current;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Run(RefreshTrigger trigger, DateTimeOffset startedAt, Activity? activity)
    {
        public RefreshTrigger Trigger { get; } = trigger;

        public string TriggerName { get; } = trigger.Name();

        public DateTimeOffset StartedAt { get; } = startedAt;

        public Activity? Activity { get; } = activity;

        public ActivityContext? Context { get; } = activity?.Context;

        public Task<ReportResult> Task { get; set; } = null!;

        // Guarded by the refresher's lock.
        public DateTimeOffset? ScheduledAt { get; set; }

        public bool Finished { get; set; }

        public bool Succeeded { get; set; }

        public bool ScheduleApplied { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A collection started by a {Running} refresh is running; the {Trigger} refresh joins it")]
    private partial void LogJoining(string trigger, string running);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refresh ({Trigger}) stored a new report: {Records} records, generated {GeneratedAt:O}")]
    private partial void LogStored(string trigger, int records, DateTimeOffset generatedAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh ({Trigger}) failed with {Kind}: {Detail} The previous report, generated {GeneratedAt:O}, is kept and served as stale")]
    private partial void LogKeptPrevious(string trigger, string kind, string detail, DateTimeOffset generatedAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refresh ({Trigger}) failed with {Kind}: {Detail} No report has been produced yet")]
    private partial void LogNoReportYet(string trigger, string kind, string detail);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Failures} refresh(es) failed in a row; next attempt in {Seconds:0} s")]
    private partial void LogRetryDue(int failures, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "The {Trigger} refresh was cancelled because the service is stopping")]
    private partial void LogCancelled(string trigger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Refresh ({Trigger}) failed unexpectedly; the previous report, if any, is kept")]
    private partial void LogRefreshThrew(Exception exception, string trigger);
}
