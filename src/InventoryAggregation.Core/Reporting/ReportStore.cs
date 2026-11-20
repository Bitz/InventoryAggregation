using System.Diagnostics;

namespace InventoryAggregation.Core.Reporting;

/// <summary>What started a refresh. Part of the response contract as <c>freshness.trigger</c>.</summary>
public enum RefreshTrigger
{
    /// <summary>The first refresh, started when the service starts.</summary>
    Startup,

    /// <summary>A refresh that fell due on the schedule, or a retry after a failure.</summary>
    Scheduled,

    /// <summary>A caller asked for <c>fresh=true</c>.</summary>
    Fresh,
}

public static class RefreshTriggerNames
{
    /// <summary>The name used in responses, span attributes and metric tags.</summary>
    public static string Name(this RefreshTrigger trigger) => trigger switch
    {
        RefreshTrigger.Startup => "startup",
        RefreshTrigger.Scheduled => "scheduled",
        RefreshTrigger.Fresh => "fresh",
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null),
    };
}

/// <summary>The latest good report and the refresh that produced it.</summary>
/// <param name="Report">The figures.</param>
/// <param name="Trigger">What started the refresh that made them.</param>
/// <param name="Refresh">That refresh's <c>refresh inventory report</c> span, or default when nothing was tracing (design.md D38).</param>
public sealed record StoredReport(CategoryReport Report, RefreshTrigger Trigger, ActivityContext Refresh = default)
{
    /// <summary>When the figures were made: the report's <c>generatedAt</c>.</summary>
    public DateTimeOffset AsOf => Report.Summary.GeneratedAt;
}

/// <summary>
/// One finished refresh attempt: when it started, what started it, and its outcome, which is
/// <see cref="Success"/> or a failure kind. <see cref="Detail"/> is set for failures and is safe to show
/// a caller.
/// </summary>
public sealed record RefreshAttempt(DateTimeOffset StartedAt, RefreshTrigger Trigger, string Outcome, string? Detail)
{
    public const string Success = "success";

    public bool Succeeded => Outcome == Success;
}

/// <summary>The store's contents at one moment; the report and the last attempt always belong together.</summary>
public sealed record ReportSnapshot(StoredReport? Latest, RefreshAttempt? LastAttempt)
{
    /// <summary>A report is stored and the latest attempt, which came after it, failed.</summary>
    public bool IsStale => Latest is not null && LastAttempt is { Succeeded: false };
}

/// <summary>
/// The latest good report, in memory only (design.md D28), and the last refresh attempt. Only a
/// successful refresh replaces the report (D7); every attempt, good or failed, replaces the last
/// attempt. Readers get an immutable snapshot, so a report is never seen with another run's attempt.
/// </summary>
public sealed class ReportStore
{
    private ReportSnapshot _snapshot = new(null, null);

    public ReportSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void RecordSuccess(CategoryReport report, RefreshTrigger trigger, DateTimeOffset startedAt, ActivityContext refresh = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        Volatile.Write(ref _snapshot, new ReportSnapshot(
            new StoredReport(report, trigger, refresh),
            new RefreshAttempt(startedAt, trigger, RefreshAttempt.Success, null)));
    }

    public void RecordFailure(InventoryFailure failure, RefreshTrigger trigger, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(failure);

        // One writer at a time (one collection at a time), so read-then-write needs no lock.
        var current = Snapshot;
        Volatile.Write(ref _snapshot, current with { LastAttempt = new RefreshAttempt(startedAt, trigger, failure.Kind, failure.Detail) });
    }
}
