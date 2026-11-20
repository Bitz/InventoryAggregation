namespace InventoryAggregation.Core;

/// <summary>What to do with records that fail validation (design.md D10).</summary>
public enum InvalidRecordPolicy
{
    /// <summary>Exclude them from every figure and list them in the response.</summary>
    Report,

    /// <summary>Refuse the whole report.</summary>
    Fail,
}

/// <summary>
/// The tuning the collection, the report and its refresh need, copied from the validated configuration by the host.
/// Defaults match design.md §8.
/// </summary>
public sealed record InventoryReportSettings
{
    public int PageSize { get; init; } = 25;

    public int MaxConcurrency { get; init; } = 4;

    public int PageRefetchLimit { get; init; } = 2;

    public int CollectionAttempts { get; init; } = 2;

    public int MaxPages { get; init; } = 1000;

    public TimeSpan CollectionTimeout { get; init; } = TimeSpan.FromMinutes(1);

    public InvalidRecordPolicy InvalidRecordPolicy { get; init; } = InvalidRecordPolicy.Report;

    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan RefreshRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan FreshMaxAge { get; init; } = TimeSpan.FromSeconds(30);
}
