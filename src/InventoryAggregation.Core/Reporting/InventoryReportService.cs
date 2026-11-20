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
/// Collect ▸ validate ▸ aggregate, once per call. When it runs and who waits for it is
/// <see cref="ReportRefresher"/>'s business (design.md D24, D27).
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
    /// <summary>Builds one report. <paramref name="trigger"/> only labels the run's telemetry.</summary>
    public async Task<ReportResult> BuildReportAsync(RefreshTrigger trigger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // One span per collection, beneath the refresh that runs it (design.md D35).
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
            var result = await CollectAndAggregateAsync(startedAt, cancellationToken).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
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
            metrics.CollectionFinished(outcome, trigger.Name(), elapsed);
            WriteManifest(outcome, activity?.TraceId.ToHexString() ?? "none", audit, failureDetail, elapsed);
        }
    }

    private async Task<ReportResult> CollectAndAggregateAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
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

    [LoggerMessage(EventId = 2600, EventName = "RunManifest", Level = LogLevel.Information, Message = "report {RunId}: run manifest: outcome {Outcome}, fingerprint {Fingerprint}; extract: {RecordsReceived} received, {DistinctRecords} distinct, {CollapsedDuplicates} duplicates collapsed, {PageRefetches} refetches, {Recollections} re-collections; transform: {Rejected} rejected, {ZeroQuantityExcluded} zero-quantity, {ConvertedToUtc} converted to UTC, {AssumedUtc} assumed UTC, {NormalizedSpellings} spellings normalized; load: {RecordsOut} records out; {DurationSeconds:0.0} s; trace {TraceId}")]
    private partial void LogManifest(string runId, string traceId, string outcome, string fingerprint, int recordsReceived, int distinctRecords, int collapsedDuplicates, int pageRefetches, int recollections, int rejected, int zeroQuantityExcluded, int convertedToUtc, int assumedUtc, int normalizedSpellings, int recordsOut, double durationSeconds);

    [LoggerMessage(EventId = 2601, EventName = "RunManifestWithoutData", Level = LogLevel.Information, Message = "report {RunId}: run manifest: outcome {Outcome}, no data loaded ({Detail}); {DurationSeconds:0.0} s; trace {TraceId}")]
    private partial void LogManifestWithoutData(string runId, string traceId, string outcome, string detail, double durationSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} record(s) failed validation (policy {Policy})")]
    private partial void LogRejected(int count, InvalidRecordPolicy policy);
}
