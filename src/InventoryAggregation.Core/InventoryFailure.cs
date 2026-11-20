using InventoryAggregation.Core.Records;

namespace InventoryAggregation.Core;

/// <summary>
/// An expected failure, carried as a value. <see cref="Kind"/> is one of <see cref="FailureKinds"/>;
/// <see cref="Detail"/> is a sentence safe to show a caller (it never contains the API key or the
/// upstream address). The optional members carry whatever evidence the failing check had.
/// </summary>
public sealed record InventoryFailure(string Kind, string Detail)
{
    /// <summary>The page the failure concerns, when it concerns one.</summary>
    public int? Page { get; init; }

    /// <summary>The last upstream status code seen, when there was a response.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Requests sent for the failing page, retries included.</summary>
    public int Attempts { get; init; }

    /// <summary>How long the upstream asked us to wait, for throttling failures.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public int? ExpectedCount { get; init; }

    public int? CollectedCount { get; init; }

    public IReadOnlyList<long> DuplicateIds { get; init; } = [];

    public IReadOnlyList<long> ConflictingIds { get; init; } = [];

    public IReadOnlyList<RejectedRecord> RejectedRecords { get; init; } = [];
}
