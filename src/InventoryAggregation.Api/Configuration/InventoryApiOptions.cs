using InventoryAggregation.Core;

namespace InventoryAggregation.Api.Configuration;

/// <summary>
/// Settings for the upstream Inventory API and the collection, bound from the <c>InventoryApi</c>
/// section. Exactly the settings in design.md §8; <see cref="InventoryApiOptionsValidator"/> enforces
/// their rules at startup.
/// </summary>
public sealed class InventoryApiOptions
{
    public const string SectionName = "InventoryApi";

    /// <summary>Absolute <c>http</c> or <c>https</c> URI of the upstream. Kept out of source control.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The upstream API key. A secret: user secrets locally, <c>InventoryApi__ApiKey</c> elsewhere.</summary>
    public string? ApiKey { get; set; }

    public string ApiKeyHeader { get; set; } = "X-Api-Key";

    public int PageSize { get; set; } = 25;

    public int MaxConcurrency { get; set; } = 4;

    public int MaxRetries { get; set; } = 3;

    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    public TimeSpan PageTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan CollectionTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public int PageRefetchLimit { get; set; } = 2;

    public int CollectionAttempts { get; set; } = 2;

    public int MaxPages { get; set; } = 1000;

    public InvalidRecordPolicy InvalidRecordPolicy { get; set; } = InvalidRecordPolicy.Report;

    public int RateLimitPermits { get; set; } = 5;

    public TimeSpan RateLimitWindow { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the stored report is refreshed, from one scheduled start to the next (design.md D30).</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The wait after a failed refresh, doubling per further failure up to <see cref="RefreshInterval"/> (design.md D32).</summary>
    public TimeSpan RefreshRetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Age past which the stored report is served with <c>overdue: true</c> (design.md D30).</summary>
    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>With <c>fresh=true</c>, a stored report younger than this is served as is (design.md D31).</summary>
    public TimeSpan FreshMaxAge { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The part of the settings Core needs.</summary>
    public InventoryReportSettings ToReportSettings() => new()
    {
        PageSize = PageSize,
        MaxConcurrency = MaxConcurrency,
        PageRefetchLimit = PageRefetchLimit,
        CollectionAttempts = CollectionAttempts,
        MaxPages = MaxPages,
        CollectionTimeout = CollectionTimeout,
        InvalidRecordPolicy = InvalidRecordPolicy,
        RefreshInterval = RefreshInterval,
        RefreshRetryDelay = RefreshRetryDelay,
        StaleAfter = StaleAfter,
        FreshMaxAge = FreshMaxAge,
    };

    /// <summary>Never prints the key or the upstream address.</summary>
    public override string ToString() =>
        $"{nameof(InventoryApiOptions)} {{ BaseUrl = {(string.IsNullOrEmpty(BaseUrl) ? "(not set)" : "(set)")}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "(not set)" : "(set)")}, PageSize = {PageSize} }}";
}
