using InventoryAggregation.Core.Records;

namespace InventoryAggregation.Core.Aggregation;

/// <summary>Figures for one category, computed from exactly the records in <see cref="Items"/>.</summary>
/// <param name="Category">The most frequent trimmed spelling in the group (design.md D11).</param>
/// <param name="TotalInventoryValue">Sum of <c>price × quantity</c>, rounded once to 2 places, half away from zero.</param>
/// <param name="AvailableRecordCount">Number of records, not units.</param>
/// <param name="MostRecentlyUpdated">Latest <c>lastUpdated</c>; ties go to the lowest id.</param>
/// <param name="Items">The records behind the figures, ordered by id.</param>
public sealed record CategorySummary(
    string Category,
    decimal TotalInventoryValue,
    int AvailableRecordCount,
    InventoryRecord MostRecentlyUpdated,
    IReadOnlyList<InventoryRecord> Items);

/// <param name="Categories">One entry per category with at least one available record, ordered by name.</param>
/// <param name="ZeroQuantityExcluded">Records dropped because their quantity is 0.</param>
public sealed record AggregationResult(IReadOnlyList<CategorySummary> Categories, int ZeroQuantityExcluded);

/// <summary>
/// Turns accepted records into per-category figures. A pure function of its input: no I/O, no clock,
/// no state between calls.
/// </summary>
public sealed class CategoryAggregator
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Registered and injected as a component.")]
    public AggregationResult Aggregate(IEnumerable<InventoryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var available = new List<InventoryRecord>();
        var zeroQuantity = 0;
        foreach (var record in records)
        {
            // Zero-quantity records count toward nothing (design.md D12).
            if (record.Quantity == 0)
            {
                zeroQuantity++;
            }
            else
            {
                available.Add(record);
            }
        }

        var categories = available
            .GroupBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
            .Select(Summarize)
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Category, StringComparer.Ordinal)
            .ToList();

        return new AggregationResult(categories, zeroQuantity);
    }

    private static CategorySummary Summarize(IEnumerable<InventoryRecord> group)
    {
        var items = group.OrderBy(r => r.Id).ToList();

        var displayName = items
            .GroupBy(r => r.Category, StringComparer.Ordinal)
            .OrderByDescending(spelling => spelling.Count())
            .ThenBy(spelling => spelling.Key, StringComparer.Ordinal)
            .First()
            .Key;

        var unrounded = 0m;
        foreach (var item in items)
        {
            unrounded += item.Price * item.Quantity;
        }

        var mostRecent = items
            .OrderByDescending(r => r.LastUpdated)
            .ThenBy(r => r.Id)
            .First();

        return new CategorySummary(
            displayName,
            Math.Round(unrounded, 2, MidpointRounding.AwayFromZero),
            items.Count,
            mostRecent,
            items);
    }
}
