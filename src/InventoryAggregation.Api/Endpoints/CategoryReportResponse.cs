using System.Text.Json.Serialization;
using InventoryAggregation.Core.Aggregation;
using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Reporting;

namespace InventoryAggregation.Api.Endpoints;

// The response contract of GET /api/inventory/categories. Timestamps are UTC DateTime values so they
// serialize with a trailing "Z".

public sealed record CategoryReportResponse(
    IReadOnlyList<CategoryResponse> Categories,
    ReportSummaryResponse Summary,
    FreshnessResponse Freshness,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] AuditResponse? Audit = null)
{
    /// <summary>The stored report with its freshness at <paramref name="now"/>; the audit is the stored run's (design.md D26).</summary>
    public static CategoryReportResponse From(StoredReport stored, RefreshStatus status, DateTimeOffset now, TimeSpan staleAfter, bool includeAudit = false)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var report = stored.Report;
        return new(
            [.. report.Categories.Select(CategoryResponse.From)],
            ReportSummaryResponse.From(report.Summary),
            FreshnessResponse.From(stored, status, now, staleAfter),
            includeAudit && report.Audit is { } audit ? AuditResponse.From(audit) : null);
    }
}

/// <summary>
/// How old the figures are and whether refreshing is failing (design.md D34). <c>asOf</c> equals
/// <c>summary.generatedAt</c>; <c>stale</c> is true when the latest attempt, which came after the
/// served report, failed; <c>overdue</c> is true when the report is older than <c>StaleAfter</c>.
/// </summary>
public sealed record FreshnessResponse(
    DateTime AsOf,
    long AgeSeconds,
    string Trigger,
    bool Stale,
    bool Overdue,
    LastRefreshResponse LastRefresh,
    DateTime NextRefreshAt)
{
    public static FreshnessResponse From(StoredReport stored, RefreshStatus status, DateTimeOffset now, TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(status);
        var age = now - stored.AsOf;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        // A stored report always has an attempt behind it; the fallback only guards the type.
        var attempt = status.Store.LastAttempt ?? new RefreshAttempt(stored.AsOf, stored.Trigger, RefreshAttempt.Success, null);
        return new(
            stored.AsOf.UtcDateTime,
            (long)Math.Floor(age.TotalSeconds),
            stored.Trigger.Name(),
            !attempt.Succeeded,
            age > staleAfter,
            LastRefreshResponse.From(attempt),
            status.NextRefreshAt.UtcDateTime);
    }
}

/// <summary>The latest refresh attempt: when it started, and <c>success</c> or the failure kind with its detail.</summary>
public sealed record LastRefreshResponse(
    DateTime StartedAt,
    string Outcome,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail)
{
    public static LastRefreshResponse From(RefreshAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return new(attempt.StartedAt.UtcDateTime, attempt.Outcome, attempt.Succeeded ? null : attempt.Detail);
    }
}

/// <summary>The run's lineage, returned with <c>?include=audit</c> (design.md D26).</summary>
public sealed record AuditResponse(
    string RunId,
    DateTime StartedAt,
    DateTime FinishedAt,
    string Fingerprint,
    ExtractAudit Extract,
    TransformAudit Transform,
    LoadAudit Load)
{
    public static AuditResponse From(RunAudit audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return new(audit.RunId, audit.StartedAt.UtcDateTime, audit.FinishedAt.UtcDateTime, audit.Fingerprint, audit.Extract, audit.Transform, audit.Load);
    }
}

public sealed record CategoryResponse(
    string Category,
    decimal TotalInventoryValue,
    int AvailableRecordCount,
    MostRecentRecordResponse MostRecentlyUpdated,
    IReadOnlyList<ItemResponse> Items)
{
    public static CategoryResponse From(CategorySummary category) => new(
        category.Category,
        category.TotalInventoryValue,
        category.AvailableRecordCount,
        new MostRecentRecordResponse(category.MostRecentlyUpdated.Id, category.MostRecentlyUpdated.Name, category.MostRecentlyUpdated.LastUpdated.UtcDateTime),
        [.. category.Items.Select(ItemResponse.From)]);
}

public sealed record MostRecentRecordResponse(long Id, string Name, DateTime LastUpdated);

public sealed record ItemResponse(long Id, string Name, decimal Price, long Quantity, DateTime LastUpdated)
{
    public static ItemResponse From(InventoryRecord record) =>
        new(record.Id, record.Name, record.Price, record.Quantity, record.LastUpdated.UtcDateTime);
}

public sealed record ReportSummaryResponse(
    int TotalRecords,
    int AvailableRecords,
    int ZeroQuantityExcluded,
    IReadOnlyList<RejectedRecordResponse> RejectedRecords,
    int AssumedUtcTimestamps,
    int PagesFetched,
    int Requests,
    DateTime GeneratedAt)
{
    public static ReportSummaryResponse From(ReportSummary summary) => new(
        summary.TotalRecords,
        summary.AvailableRecords,
        summary.ZeroQuantityExcluded,
        [.. summary.RejectedRecords.Select(RejectedRecordResponse.From)],
        summary.AssumedUtcTimestamps,
        summary.PagesFetched,
        summary.Requests,
        summary.GeneratedAt.UtcDateTime);
}

public sealed record RejectedRecordResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Id,
    IReadOnlyList<string> Reasons)
{
    public static RejectedRecordResponse From(RejectedRecord record) => new(record.Id, record.Reasons);
}
