using InventoryAggregation.Core.Aggregation;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Reporting;
using InventoryAggregation.Core.Tests.Support;

namespace InventoryAggregation.Core.Tests.Reporting;

public sealed class RunAuditBuilderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Scenario", "data-lineage/Category spellings")]
    public void Each_raw_spelling_is_listed_with_its_count_under_the_name_it_became()
    {
        var records = Enumerable.Range(1, 14)
            .Select(i => UpstreamData.Record(i, category: i <= 4 ? "  toys " : "Toys"))
            .ToList();

        var audit = Audit(records);

        var toys = Assert.Single(audit.Transform.Categories);
        Assert.Equal("Toys", toys.Category);
        Assert.Equal([new SpellingCount("Toys", 10), new SpellingCount("  toys ", 4)], toys.Spellings);
        Assert.Equal(4, RunAuditBuilder.NormalizedSpellings(audit));
    }

    [Fact]
    [Trait("Scenario", "data-lineage/Timestamp corrections")]
    public void Converted_and_assumed_timestamps_are_listed_by_id()
    {
        var audit = Audit(
        [
            UpstreamData.Record(1, lastUpdated: "2026-10-15T14:30:00+02:00"),
            UpstreamData.Record(2, lastUpdated: "2026-10-15T12:30:00"),
            UpstreamData.Record(3, lastUpdated: "2026-10-15T12:30:00Z"),
        ]);

        Assert.Equal([1L], audit.Transform.Timestamps.ConvertedToUtc);
        Assert.Equal([2L], audit.Transform.Timestamps.AssumedUtc);
    }

    [Fact]
    [Trait("Scenario", "data-lineage/A category that drops out")]
    public void A_category_with_only_zero_quantity_is_listed_as_excluded_with_its_ids()
    {
        var audit = Audit(
        [
            UpstreamData.Record(1151, category: "Discontinued", quantity: 0),
            UpstreamData.Record(1152, category: " discontinued", quantity: 0),
            UpstreamData.Record(1001, category: "Toys", quantity: 3),
        ], out var aggregation);

        var excluded = Assert.Single(audit.Transform.ZeroQuantityExcluded);
        Assert.Equal("Discontinued", excluded.Category);
        Assert.Equal([1151L, 1152L], excluded.Ids);
        Assert.DoesNotContain(aggregation.Categories, c => c.Category == "Discontinued");
        Assert.Contains(audit.Load.Categories, c => c is { Category: "Discontinued", RecordsIn: 2, ZeroQuantityExcluded: 2, RecordsOut: 0 });
    }

    [Fact]
    [Trait("Scenario", "data-lineage/Counts reconcile")]
    public void Records_in_equal_excluded_plus_out_and_the_totals_match_the_dataset()
    {
        var data = new UpstreamData();
        for (var i = 0; i < 157; i += 9)
        {
            data.Records[i] = data.Records[i] with { Quantity = 0 };
        }

        data.Records[40] = data.Records[40] with { Price = -1m };

        var audit = Audit(data.Records);

        Assert.All(audit.Load.Categories, c => Assert.Equal(c.RecordsIn, c.ZeroQuantityExcluded + c.RecordsOut));
        Assert.Equal(audit.Load.RecordsIn, audit.Load.ZeroQuantityExcluded + audit.Load.RecordsOut);
        Assert.Equal(157, audit.Load.RecordsIn + audit.Load.Rejected);
        Assert.Equal(1, audit.Load.Rejected);
    }

    [Fact]
    [Trait("Scenario", "data-lineage/Same data")]
    public void The_same_data_in_any_order_has_the_same_fingerprint()
    {
        var data = new UpstreamData();
        var reversed = Enumerable.Reverse(data.Records).ToList();

        var first = RunAuditBuilder.Fingerprint(data.Records);

        Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
        Assert.Equal(71, first.Length);
        Assert.Equal(first, RunAuditBuilder.Fingerprint(reversed));
    }

    [Fact]
    [Trait("Scenario", "data-lineage/Changed data")]
    public void A_changed_price_changes_the_fingerprint()
    {
        var data = new UpstreamData();
        var before = RunAuditBuilder.Fingerprint(data.Records);
        data.Records[41] = data.Records[41] with { Price = data.Records[41].Price + 0.01m };

        Assert.NotEqual(before, RunAuditBuilder.Fingerprint(data.Records));
    }

    private static RunAudit Audit(IReadOnlyList<RawRecord> records) => Audit(records, out _);

    private static RunAudit Audit(IReadOnlyList<RawRecord> records, out AggregationResult aggregation)
    {
        var dataset = new CollectedDataset(records, records.Count, 1, 2, 0, 1, []);
        var validation = new RecordValidator().Validate(records);
        aggregation = new CategoryAggregator().Aggregate(validation.Accepted);
        return RunAuditBuilder.Build("abc123", Start, Start.AddSeconds(10), dataset, validation, aggregation);
    }
}
