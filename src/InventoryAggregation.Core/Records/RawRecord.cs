namespace InventoryAggregation.Core.Records;

/// <summary>
/// One inventory row exactly as the upstream sent it, before anyone has judged it. Every field is
/// optional so that one bad record cannot fail the page it arrived on; <see cref="RecordValidator"/>
/// decides whether it may feed a figure.
/// </summary>
public sealed record RawRecord
{
    public long? Id { get; init; }

    public string? Name { get; init; }

    public string? Category { get; init; }

    /// <summary>Read straight from the JSON number into <see cref="decimal"/>, never through a binary float.</summary>
    public decimal? Price { get; init; }

    /// <summary>Held as <see cref="decimal"/> so a fractional quantity can be reported rather than fail the page.</summary>
    public decimal? Quantity { get; init; }

    public string? LastUpdated { get; init; }

    /// <summary>
    /// Fields that were present but held a value of the wrong type (for example a string price).
    /// Their typed property is <see langword="null"/>; this set tells "wrong type" apart from "missing".
    /// </summary>
    public IReadOnlyCollection<string> MalformedFields { get; init; } = [];

    // Record equality would compare MalformedFields by reference. Two records are the same record
    // when every field, including which fields were malformed, is the same (design.md D9).
    public bool Equals(RawRecord? other) =>
        other is not null
        && Id == other.Id
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(Category, other.Category, StringComparison.Ordinal)
        && Price == other.Price
        && Quantity == other.Quantity
        && string.Equals(LastUpdated, other.LastUpdated, StringComparison.Ordinal)
        && MalformedFields.Order(StringComparer.Ordinal).SequenceEqual(other.MalformedFields.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    public override int GetHashCode() =>
        HashCode.Combine(Id, Name, Category, Price, Quantity, LastUpdated, MalformedFields.Count);
}

/// <summary>Upstream field names, as used in <see cref="RawRecord.MalformedFields"/> and in rejection reasons.</summary>
public static class RecordFields
{
    public const string Id = "id";
    public const string Name = "name";
    public const string Category = "category";
    public const string Price = "price";
    public const string Quantity = "quantity";
    public const string LastUpdated = "lastUpdated";
}
