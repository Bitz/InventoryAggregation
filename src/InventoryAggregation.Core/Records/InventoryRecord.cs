namespace InventoryAggregation.Core.Records;

/// <summary>A record that passed validation, normalized: category trimmed, timestamp in UTC, money exact.</summary>
public sealed record InventoryRecord(
    long Id,
    string Name,
    string Category,
    decimal Price,
    long Quantity,
    DateTimeOffset LastUpdated)
{
    /// <summary>The category exactly as the upstream sent it, before trimming; kept for the audit (design.md D26).</summary>
    public string? RawCategory { get; init; }
}

/// <summary>A record that failed validation, with every reason that applies.</summary>
public sealed record RejectedRecord(long? Id, IReadOnlyList<string> Reasons);

/// <summary>The validator's verdict on a set of raw records.</summary>
/// <param name="Accepted">Records that may feed figures, in input order.</param>
/// <param name="Rejected">Records excluded from every figure, in input order.</param>
/// <param name="AssumedUtcTimestamps">Accepted records whose <c>lastUpdated</c> had no offset and was read as UTC.</param>
public sealed record ValidationResult(
    IReadOnlyList<InventoryRecord> Accepted,
    IReadOnlyList<RejectedRecord> Rejected,
    int AssumedUtcTimestamps)
{
    /// <summary>Accepted ids whose <c>lastUpdated</c> carried a non-zero offset and was converted to UTC.</summary>
    public IReadOnlyList<long> ConvertedToUtcIds { get; init; } = [];

    /// <summary>Accepted ids whose <c>lastUpdated</c> had no offset and was read as UTC.</summary>
    public IReadOnlyList<long> AssumedUtcIds { get; init; } = [];
}
