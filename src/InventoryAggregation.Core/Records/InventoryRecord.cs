namespace InventoryAggregation.Core.Records;

/// <summary>A record that passed validation, normalized: category trimmed, timestamp in UTC, money exact.</summary>
public sealed record InventoryRecord(
    long Id,
    string Name,
    string Category,
    decimal Price,
    long Quantity,
    DateTimeOffset LastUpdated);

/// <summary>A record that failed validation, with every reason that applies.</summary>
public sealed record RejectedRecord(long? Id, IReadOnlyList<string> Reasons);

/// <summary>The validator's verdict on a set of raw records.</summary>
/// <param name="Accepted">Records that may feed figures, in input order.</param>
/// <param name="Rejected">Records excluded from every figure, in input order.</param>
/// <param name="AssumedUtcTimestamps">Accepted records whose <c>lastUpdated</c> had no offset and was read as UTC.</param>
public sealed record ValidationResult(
    IReadOnlyList<InventoryRecord> Accepted,
    IReadOnlyList<RejectedRecord> Rejected,
    int AssumedUtcTimestamps);
