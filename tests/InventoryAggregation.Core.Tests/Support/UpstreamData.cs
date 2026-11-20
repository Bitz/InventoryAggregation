using System.Globalization;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Records;

namespace InventoryAggregation.Core.Tests.Support;

/// <summary>
/// A static dataset shaped like the one the upstream serves: contiguous ids from 1001, and pages that
/// can repeat the previous page's last record the way every even-numbered upstream page does.
/// </summary>
internal sealed class UpstreamData
{
    public UpstreamData(int count = 157)
    {
        Records = [.. Enumerable.Range(0, count).Select(i => Record(1001 + i))];
    }

    public UpstreamData(IEnumerable<RawRecord> records)
    {
        Records = [.. records];
    }

    public List<RawRecord> Records { get; }

    public int TotalRecords => Records.Count;

    public int TotalPages(int pageSize) => (int)Math.Ceiling(TotalRecords / (double)pageSize);

    /// <summary>The page as the upstream returns it. With <paramref name="overlap"/>, even pages repeat the previous page's last record first.</summary>
    public InventoryPage Page(int page, int pageSize, bool overlap = false)
    {
        var records = Records.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        if (overlap && page % 2 == 0 && page > 1 && records.Count > 0)
        {
            records.Insert(0, Records[((page - 1) * pageSize) - 1]);
        }

        return Envelope(page, pageSize, records);
    }

    public InventoryPage Envelope(int page, int pageSize, IReadOnlyList<RawRecord> records) =>
        new(page, pageSize, TotalPages(pageSize), TotalRecords, records);

    /// <summary>A script that serves this dataset faithfully, empty pages past the end included.</summary>
    public Func<ScriptedCall, PageResult> Healthy(bool overlap = true) =>
        call => ScriptedPageSource.Ok(Page(call.Page, call.PageSize, overlap));

    public static RawRecord Record(
        long id,
        string? category = null,
        decimal price = 10m,
        decimal quantity = 1m,
        string? lastUpdated = null) => new()
    {
        Id = id,
        Name = $"Item {id}",
        Category = category ?? Categories[(int)(id % Categories.Length)],
        Price = price,
        Quantity = quantity,
        LastUpdated = lastUpdated ?? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)
            .AddMinutes(id)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
    };

    private static readonly string[] Categories = ["Electronics", " toys ", "Office Supplies", "OUTDOOR "];
}
