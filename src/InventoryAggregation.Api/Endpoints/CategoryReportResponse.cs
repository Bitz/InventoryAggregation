using System.Text.Json.Serialization;
using InventoryAggregation.Core.Aggregation;
using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Reporting;

namespace InventoryAggregation.Api.Endpoints;

// The response contract of GET /api/inventory/categories. Timestamps are UTC DateTime values so they
// serialize with a trailing "Z".

public sealed record CategoryReportResponse(IReadOnlyList<CategoryResponse> Categories, ReportSummaryResponse Summary)
{
    public static CategoryReportResponse From(CategoryReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new(
            [.. report.Categories.Select(CategoryResponse.From)],
            ReportSummaryResponse.From(report.Summary));
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
