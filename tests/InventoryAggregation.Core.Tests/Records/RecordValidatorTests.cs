using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Tests.Support;

namespace InventoryAggregation.Core.Tests.Records;

public sealed class RecordValidatorTests
{
    private readonly RecordValidator _validator = new();

    [Fact]
    [Trait("Scenario", "inventory-records/Several problems")]
    public void Every_reason_is_reported_not_just_the_first()
    {
        var record = UpstreamData.Record(1001) with { Category = "   ", Quantity = -2 };

        var result = _validator.Validate([record]);

        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(1001, rejected.Id);
        Assert.Equal(["category is blank", "quantity is negative"], rejected.Reasons);
        Assert.Empty(result.Accepted);
    }

    [Fact]
    [Trait("Scenario", "inventory-records/Timestamp without offset")]
    public void A_timestamp_without_offset_is_read_as_utc_and_counted()
    {
        var record = UpstreamData.Record(1001, lastUpdated: "2026-10-15T12:30:00");

        var result = _validator.Validate([record]);

        var accepted = Assert.Single(result.Accepted);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.Zero), accepted.LastUpdated);
        Assert.Equal(TimeSpan.Zero, accepted.LastUpdated.Offset);
        Assert.Equal(1, result.AssumedUtcTimestamps);
    }

    [Fact]
    [Trait("Scenario", "inventory-records/Timestamp that does not parse")]
    public void An_unparseable_timestamp_is_rejected()
    {
        var record = UpstreamData.Record(1001, lastUpdated: "yesterday");

        var result = _validator.Validate([record]);

        var rejected = Assert.Single(result.Rejected);
        Assert.Equal(["lastUpdated is not an ISO 8601 timestamp"], rejected.Reasons);
    }

    [Theory]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("15/10/2026 12:30")]
    [InlineData("2026-10-15")]
    [InlineData("")]
    public void Other_non_iso_timestamps_are_rejected(string value)
    {
        var result = _validator.Validate([UpstreamData.Record(1001, lastUpdated: value)]);

        Assert.Single(result.Rejected);
    }

    [Fact]
    [Trait("Scenario", "inventory-records/Zero quantity is valid")]
    public void Zero_quantity_is_accepted()
    {
        var result = _validator.Validate([UpstreamData.Record(1001, quantity: 0)]);

        var accepted = Assert.Single(result.Accepted);
        Assert.Equal(0, accepted.Quantity);
    }

    [Fact]
    [Trait("Scenario", "inventory-records/Offset converted")]
    public void An_offset_is_converted_to_utc()
    {
        var result = _validator.Validate([UpstreamData.Record(1001, lastUpdated: "2026-10-15T14:30:00+02:00")]);

        var accepted = Assert.Single(result.Accepted);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 12, 30, 0, TimeSpan.Zero), accepted.LastUpdated);
        Assert.Equal(TimeSpan.Zero, accepted.LastUpdated.Offset);
        Assert.Equal(0, result.AssumedUtcTimestamps);
    }

    [Fact]
    [Trait("Scenario", "inventory-records/Exact money")]
    public void Price_is_held_exactly()
    {
        var result = _validator.Validate([UpstreamData.Record(1001, price: 0.1m)]);

        Assert.Equal(0.1m, Assert.Single(result.Accepted).Price);
    }

    [Fact]
    public void Category_is_trimmed_and_name_kept()
    {
        var result = _validator.Validate([UpstreamData.Record(1001, category: "  Toys ")]);

        var accepted = Assert.Single(result.Accepted);
        Assert.Equal("Toys", accepted.Category);
        Assert.Equal("Item 1001", accepted.Name);
    }

    [Fact]
    public void Missing_and_wrongly_typed_fields_are_told_apart()
    {
        var record = new RawRecord
        {
            Id = null,
            Name = null,
            Category = "Toys",
            Price = null,
            Quantity = 1.5m,
            LastUpdated = "2026-10-15T12:30:00Z",
            MalformedFields = [RecordFields.Price],
        };

        var result = _validator.Validate([record]);

        var rejected = Assert.Single(result.Rejected);
        Assert.Null(rejected.Id);
        Assert.Equal(["id is missing", "name is missing", "price is not a number", "quantity is not a whole number"], rejected.Reasons);
    }

    [Fact]
    [Trait("Scenario", "inventory-records/Tested without a network")]
    public void Validation_runs_on_in_memory_records_with_no_transport_types()
    {
        // The records are built in memory; the Core assembly that holds the validator references
        // nothing that could reach a network or parse JSON.
        var result = _validator.Validate([UpstreamData.Record(1001), UpstreamData.Record(1002) with { Price = -1 }]);

        Assert.Single(result.Accepted);
        Assert.Single(result.Rejected);
        Assert.Empty(CoreAssembly.TransportReferences());
    }
}
