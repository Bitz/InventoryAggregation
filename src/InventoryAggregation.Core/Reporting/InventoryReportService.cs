using System.Diagnostics.CodeAnalysis;
using InventoryAggregation.Core.Aggregation;
using InventoryAggregation.Core.Collection;
using System.Diagnostics;
using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InventoryAggregation.Core.Reporting;

/// <summary>How every record of the collected dataset was accounted for, and what the collection cost.</summary>
public sealed record ReportSummary(
    int TotalRecords,
    int AvailableRecords,
    int ZeroQuantityExcluded,
    IReadOnlyList<RejectedRecord> RejectedRecords,
    int AssumedUtcTimestamps,
    int PagesFetched,
    int Requests,
    DateTimeOffset GeneratedAt);

/// <summary>The category report: figures per category and the summary that accounts for them.</summary>
public sealed record CategoryReport(IReadOnlyList<CategorySummary> Categories, ReportSummary Summary)
{
    /// <summary>The lineage of the run (design.md D26). Always built; the endpoint shows it on request.</summary>
    public RunAudit? Audit { get; init; }
}

/// <summary>A report, or the reason there is none.</summary>
public sealed class ReportResult
{
    private ReportResult(CategoryReport? report, InventoryFailure? failure)
    {
        Report = report;
        Failure = failure;
    }

    public CategoryReport? Report { get; }

    public InventoryFailure? Failure { get; }

    [MemberNotNullWhen(true, nameof(Report))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess => Report is not null;

    public static ReportResult Success(CategoryReport report) => new(report, null);

    public static ReportResult Failed(InventoryFailure failure) => new(null, failure);
}

/// <summary>
/// Collect ▸ validate ▸ aggregate. Requests that arrive while a collection is running share it
/// (design.md D24): the upstream sees one collection, every waiting caller gets the same outcome, and
/// nothing is kept once it has been handed out. The shared run is cancelled only when every caller
/// waiting on it has gone.
/// </summary>
public sealed partial class InventoryReportService(
    IServiceScopeFactory scopeFactory,
    RecordValidator validator,
    CategoryAggregator aggregator,
    InventoryReportSettings settings,
    TimeProvider timeProvider,
    InventoryMetrics metrics,
    ILogger<InventoryReportService> logger)
{
    private readonly Lock _gate = new();
    private SharedRun? _current;

    public async Task<ReportResult> GetReportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        SharedRun run;
        lock (_gate)
        {
            if (_current is null)
            {
                var started = new SharedRun();
                _current = started;
                started.Task = Task.Run(() => RunAsync(started));
            }
            else
            {
                LogJoining(_current.Waiters);
            }

            run = _current;
            run.Waiters++;
        }

        try
        {
            return await run.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Leave(run);
        }
    }

    private void Leave(SharedRun run)
    {
        lock (_gate)
        {
            run.Waiters--;
            if (run.Waiters > 0 || run.Task.IsCompleted)
            {
                return;
            }

            // Every caller has gone: stop the upstream work, and let the next caller start afresh.
            if (ReferenceEquals(_current, run))
            {
                _current = null;
            }
        }

        LogAbandoned();
        run.Cancellation.Cancel();
    }

    private async Task<ReportResult> RunAsync(SharedRun run)
    {
        // One span per shared collection, a child of the request that started it (design.md D25).
        using var activity = InventoryTelemetry.ActivitySource.StartActivity("collect inventory");

        // A short id for progress lines: the trace id's start when there is a trace, so a log line leads
        // to its trace; otherwise random.
        ReportRun.Begin(activity is not null
            ? activity.TraceId.ToHexString()[..6]
            : Convert.ToHexStringLower(BitConverter.GetBytes(Random.Shared.Next()))[..6]);
        var started = timeProvider.GetTimestamp();
        var startedAt = timeProvider.GetUtcNow();
        var outcome = "internal-error";
        RunAudit? audit = null;
        string? failureDetail = null;
        try
        {
            var result = await BuildReportAsync(startedAt, run.Cancellation.Token).ConfigureAwait(false);
            outcome = result.IsSuccess ? "success" : result.Failure.Kind;
            audit = result.Report?.Audit;
            failureDetail = result.Failure?.Detail;
            if (result.IsSuccess)
            {
                activity?.SetTag("inventory.pages", result.Report.Summary.PagesFetched);
                activity?.SetTag("inventory.requests", result.Report.Summary.Requests);
                activity?.SetTag("inventory.records", result.Report.Summary.TotalRecords);
            }
            else
            {
                activity?.SetStatus(ActivityStatusCode.Error, result.Failure.Detail);
            }

            return result;
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            throw;
        }
        finally
        {
            activity?.SetTag("inventory.outcome", outcome);
            var elapsed = timeProvider.GetElapsedTime(started);
            metrics.CollectionFinished(outcome, elapsed);
            WriteManifest(outcome, activity?.TraceId.ToHexString() ?? "none", audit, failureDetail, elapsed);
            lock (_gate)
            {
                if (ReferenceEquals(_current, run))
                {
                    _current = null;
                }
            }
        }
    }

    private async Task<ReportResult> BuildReportAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        CollectionResult collected;

        // A fresh scope per collection: the collector and its typed HTTP client come from the container
        // for this run and are released with it.
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var collector = scope.ServiceProvider.GetRequiredService<InventoryCollector>();
            collected = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!collected.IsSuccess)
        {
            return ReportResult.Failed(collected.Failure);
        }

        var dataset = collected.Dataset;
        var validation = validator.Validate(dataset.Records);
        foreach (var reason in validation.Rejected.SelectMany(r => r.Reasons))
        {
            metrics.RecordRejected(reason);
        }

        if (validation.Rejected.Count > 0)
        {
            LogRejected(validation.Rejected.Count, settings.InvalidRecordPolicy);
            if (settings.InvalidRecordPolicy == InvalidRecordPolicy.Fail)
            {
                return ReportResult.Failed(new InventoryFailure(
                    FailureKinds.InvalidRecords,
                    $"{validation.Rejected.Count} record(s) failed validation and the policy is to fail the report.")
                {
                    RejectedRecords = validation.Rejected,
                });
            }
        }

        var aggregation = aggregator.Aggregate(validation.Accepted);

        var summary = new ReportSummary(
            TotalRecords: dataset.TotalRecords,
            AvailableRecords: aggregation.Categories.Sum(c => c.AvailableRecordCount),
            ZeroQuantityExcluded: aggregation.ZeroQuantityExcluded,
            RejectedRecords: validation.Rejected,
            AssumedUtcTimestamps: validation.AssumedUtcTimestamps,
            PagesFetched: dataset.PagesFetched,
            Requests: dataset.Requests,
            GeneratedAt: timeProvider.GetUtcNow());

        var audit = RunAuditBuilder.Build(ReportRun.Id, startedAt, summary.GeneratedAt, dataset, validation, aggregation);
        metrics.DuplicatesCollapsed(audit.Extract.CollapsedDuplicates.Count);
        metrics.SpellingsNormalized(RunAuditBuilder.NormalizedSpellings(audit));

        return ReportResult.Success(new CategoryReport(aggregation.Categories, summary) { Audit = audit });
    }

    /// <summary>One structured record per run, so a run can be audited from the log backend alone (design.md D26).</summary>
    private void WriteManifest(string outcome, string traceId, RunAudit? audit, string? failureDetail, TimeSpan elapsed)
    {
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return;
        }

        var seconds = elapsed.TotalSeconds;
        if (audit is null)
        {
            LogManifestWithoutData(ReportRun.Id, traceId, outcome, failureDetail ?? "none", seconds);
            return;
        }

        var normalized = RunAuditBuilder.NormalizedSpellings(audit);
        LogManifest(
            ReportRun.Id,
            traceId,
            outcome,
            audit.Fingerprint,
            audit.Extract.RecordsReceived,
            audit.Extract.DistinctRecords,
            audit.Extract.CollapsedDuplicates.Count,
            audit.Extract.PageRefetches.Count,
            audit.Extract.Recollections.Count,
            audit.Load.Rejected,
            audit.Load.ZeroQuantityExcluded,
            audit.Transform.Timestamps.ConvertedToUtc.Count,
            audit.Transform.Timestamps.AssumedUtc.Count,
            normalized,
            audit.Load.RecordsOut,
            seconds);
    }

    private sealed class SharedRun
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public Task<ReportResult> Task { get; set; } = null!;

        public int Waiters { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A report collection is already running; joining it ({Waiters} caller(s) already waiting)")]
    private partial void LogJoining(int waiters);

    [LoggerMessage(Level = LogLevel.Information, Message = "Every caller waiting on the report collection has gone; stopping it")]
    private partial void LogAbandoned();

    [LoggerMessage(EventId = 2600, EventName = "RunManifest", Level = LogLevel.Information, Message = "report {RunId}: run manifest: outcome {Outcome}, fingerprint {Fingerprint}; extract: {RecordsReceived} received, {DistinctRecords} distinct, {CollapsedDuplicates} duplicates collapsed, {PageRefetches} refetches, {Recollections} re-collections; transform: {Rejected} rejected, {ZeroQuantityExcluded} zero-quantity, {ConvertedToUtc} converted to UTC, {AssumedUtc} assumed UTC, {NormalizedSpellings} spellings normalized; load: {RecordsOut} records out; {DurationSeconds:0.0} s; trace {TraceId}")]
    private partial void LogManifest(string runId, string traceId, string outcome, string fingerprint, int recordsReceived, int distinctRecords, int collapsedDuplicates, int pageRefetches, int recollections, int rejected, int zeroQuantityExcluded, int convertedToUtc, int assumedUtc, int normalizedSpellings, int recordsOut, double durationSeconds);

    [LoggerMessage(EventId = 2601, EventName = "RunManifestWithoutData", Level = LogLevel.Information, Message = "report {RunId}: run manifest: outcome {Outcome}, no data loaded ({Detail}); {DurationSeconds:0.0} s; trace {TraceId}")]
    private partial void LogManifestWithoutData(string runId, string traceId, string outcome, string detail, double durationSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} record(s) failed validation (policy {Policy})")]
    private partial void LogRejected(int count, InvalidRecordPolicy policy);
}
