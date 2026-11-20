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
public sealed record CategoryReport(IReadOnlyList<CategorySummary> Categories, ReportSummary Summary);

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
        var started = timeProvider.GetTimestamp();
        var outcome = "internal-error";
        try
        {
            var result = await BuildReportAsync(run.Cancellation.Token).ConfigureAwait(false);
            outcome = result.IsSuccess ? "success" : result.Failure.Kind;
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
            metrics.CollectionFinished(outcome, timeProvider.GetElapsedTime(started));
            lock (_gate)
            {
                if (ReferenceEquals(_current, run))
                {
                    _current = null;
                }
            }
        }
    }

    private async Task<ReportResult> BuildReportAsync(CancellationToken cancellationToken)
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

        return ReportResult.Success(new CategoryReport(aggregation.Categories, summary));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} record(s) failed validation (policy {Policy})")]
    private partial void LogRejected(int count, InvalidRecordPolicy policy);
}
