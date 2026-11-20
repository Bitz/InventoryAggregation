using InventoryAggregation.Core.Aggregation;
using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Tests.Support;

namespace InventoryAggregation.Core.Tests.Aggregation;

public sealed class CategoryAggregatorTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly CategoryAggregator _aggregator = new();

    [Fact]
    [Trait("Scenario", "category-aggregation/Mixed category")]
    public void Zero_quantity_records_count_toward_nothing()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Electronics", 10m, 1),
            Record(2, "Electronics", 20m, 0, minutes: 99),
            Record(3, "Electronics", 30m, 2),
        ]);

        var electronics = Assert.Single(result.Categories);
        Assert.Equal(2, electronics.AvailableRecordCount);
        Assert.Equal([1L, 3L], electronics.Items.Select(i => i.Id));
        Assert.Equal(70m, electronics.TotalInventoryValue);
        Assert.Equal(3, electronics.MostRecentlyUpdated.Id);
        Assert.Equal(1, result.ZeroQuantityExcluded);
    }

    [Fact]
    [Trait("Scenario", "category-aggregation/All out of stock")]
    public void A_category_with_only_zero_quantity_records_does_not_appear()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Garden", 5m, 0),
            Record(2, "Garden", 6m, 0),
            Record(3, "Toys", 1m, 1),
        ]);

        Assert.Equal(["Toys"], result.Categories.Select(c => c.Category));
        Assert.Equal(2, result.ZeroQuantityExcluded);
    }

    [Fact]
    [Trait("Scenario", "category-aggregation/Spelling variants")]
    public void Spelling_variants_form_one_category_named_by_the_most_frequent()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1, "Electronics", 1m, 1),
            Record(2, "electronics", 1m, 1),
            Record(3, "Electronics", 1m, 1),
        ]);

        var category = Assert.Single(result.Categories);
        Assert.Equal("Electronics", category.Category);
        Assert.Equal(3, category.AvailableRecordCount);
    }

    [Fact]
    public void A_tie_between_spellings_goes_to_ordinal_order()
    {
        var result = _aggregator.Aggregate([Record(1, "toys", 1m, 1), Record(2, "Toys", 1m, 1)]);

        Assert.Equal("Toys", Assert.Single(result.Categories).Category);
    }

    [Fact]
    [Trait("Scenario", "category-aggregation/Value")]
    public void Value_is_summed_exactly_and_rounded_once_away_from_zero()
    {
        var result = _aggregator.Aggregate([Record(1, "Electronics", 149.99m, 12), Record(2, "Electronics", 0.005m, 1)]);

        Assert.Equal(1799.89m, Assert.Single(result.Categories).TotalInventoryValue);
    }

    [Fact]
    public void Rounding_happens_once_not_per_item()
    {
        // Rounded per item, each 0.004 would vanish; summed first, 3 × 0.004 = 0.012 rounds to 0.01.
        var result = _aggregator.Aggregate(
            [Record(1, "A", 0.004m, 1), Record(2, "A", 0.004m, 1), Record(3, "A", 0.004m, 1)]);

        Assert.Equal(0.01m, Assert.Single(result.Categories).TotalInventoryValue);
    }

    [Fact]
    [Trait("Scenario", "category-aggregation/Tie on lastUpdated")]
    public void A_tie_on_last_updated_goes_to_the_lowest_id()
    {
        var result = _aggregator.Aggregate(
        [
            Record(1007, "Toys", 1m, 1, minutes: 50),
            Record(1003, "Toys", 1m, 1, minutes: 50),
            Record(1001, "Toys", 1m, 1, minutes: 10),
        ]);

        Assert.Equal(1003, Assert.Single(result.Categories).MostRecentlyUpdated.Id);
    }

    [Fact]
    [Trait("Scenario", "category-aggregation/Items match figures")]
    public void Items_are_exactly_the_records_behind_the_figures()
    {
        var result = _aggregator.Aggregate(
        [
            Record(4, "Outdoor", 12.5m, 3),
            Record(1, "Outdoor", 7.25m, 2),
            Record(3, "Outdoor", 1m, 0),
            Record(2, "Outdoor", 100m, 1),
            Record(5, "Outdoor", 0.99m, 10),
        ]);

        var category = Assert.Single(result.Categories);
        Assert.Equal(4, category.AvailableRecordCount);
        Assert.Equal(4, category.Items.Count);
        Assert.Equal([1L, 2L, 4L, 5L], category.Items.Select(i => i.Id));
        Assert.Equal(category.TotalInventoryValue, category.Items.Sum(i => i.Price * i.Quantity));
        Assert.Contains(category.MostRecentlyUpdated, category.Items);
    }

    [Fact]
    [Trait("Scenario", "category-aggregation/Same input, same output")]
    public void Input_order_does_not_change_the_output()
    {
        var records = Enumerable.Range(1, 60)
            .Select(i => Record(i, Spelling(i), i * 1.01m, i % 5, minutes: i % 7))
            .ToList();
        var shuffled = records.OrderBy(r => (r.Id * 37) % 61).ToList();

        var first = _aggregator.Aggregate(records);
        var second = _aggregator.Aggregate(shuffled);

        Assert.Equal(Describe(first), Describe(second));
        // Each pair of spellings ties at 12 available records, so ordinal order picks the name.
        Assert.Equal(["OFFICE SUPPLIES", "Toys"], first.Categories.Select(c => c.Category));
    }

    [Fact]
    [Trait("Scenario", "category-aggregation/Tested in memory")]
    public void Aggregation_needs_no_host_network_clock_or_configuration()
    {
        // Constructed with no arguments: nothing to inject, so nothing to read but the records.
        Assert.Empty(typeof(CategoryAggregator).GetConstructors().Single().GetParameters());
        Assert.Empty(CoreAssembly.TransportReferences());

        var result = new CategoryAggregator().Aggregate([Record(1, "Toys", 2m, 3)]);

        Assert.Equal(6m, Assert.Single(result.Categories).TotalInventoryValue);
    }

    private static InventoryRecord Record(long id, string category, decimal price, long quantity, int minutes = 0) =>
        new(id, $"Item {id}", category, price, quantity, Base.AddMinutes(minutes == 0 ? id : minutes));

    private static string Spelling(int i) => (i % 4) switch
    {
        0 => "Toys",
        1 => "toys",
        2 => "Office Supplies",
        _ => "OFFICE SUPPLIES",
    };

    private static string Describe(AggregationResult result) =>
        string.Join(
            "\n",
            result.Categories.Select(c =>
                $"{c.Category}|{c.TotalInventoryValue}|{c.AvailableRecordCount}|{c.MostRecentlyUpdated}|{string.Join(",", c.Items)}"))
        + $"\n{result.ZeroQuantityExcluded}";
}
