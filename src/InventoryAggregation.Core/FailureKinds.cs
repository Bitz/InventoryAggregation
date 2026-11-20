namespace InventoryAggregation.Core;

/// <summary>
/// The named reasons a page, a collection or a report can fail. Each value is part of the public
/// contract: a collection's kind appears as <c>freshness.lastRefresh.outcome</c>, and the two response
/// kinds (<see cref="ReportNotReady"/>, <see cref="InternalError"/>) as the <c>kind</c> of a Problem
/// Details response and in its <c>type</c> URN (design.md D16).
/// </summary>
public static class FailureKinds
{
    /// <summary>Transport errors, timeouts or transient statuses that outlasted the retries.</summary>
    public const string UpstreamUnavailable = "upstream-unavailable";

    /// <summary><c>429</c> (or <c>503</c> with <c>Retry-After</c>) beyond the cap, or after retries.</summary>
    public const string UpstreamThrottled = "upstream-throttled";

    /// <summary>The whole collection exceeded <c>CollectionTimeout</c>.</summary>
    public const string UpstreamTimeout = "upstream-timeout";

    /// <summary><c>400</c>, <c>401</c>, <c>403</c> or <c>404</c> on an in-range request: our configuration is wrong.</summary>
    public const string UpstreamMisconfigured = "upstream-misconfigured";

    /// <summary>Any other <c>4xx</c>.</summary>
    public const string UpstreamRejected = "upstream-rejected";

    /// <summary>A status no rule covers, such as an unfollowed <c>3xx</c>, <c>501</c> or a <c>2xx</c> other than <c>200</c>.</summary>
    public const string UpstreamUnexpectedStatus = "upstream-unexpected-status";

    /// <summary>A <c>200</c> whose body does not parse into the page envelope, after refetches.</summary>
    public const string MalformedResponse = "malformed-response";

    /// <summary>Page metadata or record count disagrees with the reference, or records exist past the last page.</summary>
    public const string InconsistentPaging = "inconsistent-paging";

    /// <summary>The count of distinct ids does not equal <c>totalRecords</c> after re-collection.</summary>
    public const string IncompleteDataset = "incomplete-dataset";

    /// <summary>The same id arrived with different content after re-collection.</summary>
    public const string ConflictingRecords = "conflicting-records";

    /// <summary>At least one record was rejected while <c>InvalidRecordPolicy</c> is <c>Fail</c>.</summary>
    public const string InvalidRecords = "invalid-records";

    /// <summary>Page 1 reported more pages than <c>MaxPages</c>.</summary>
    public const string DatasetTooLarge = "dataset-too-large";

    /// <summary>An unhandled exception in the service itself.</summary>
    public const string InternalError = "internal-error";

    /// <summary>No good report has been produced since the service started (design.md D29).</summary>
    public const string ReportNotReady = "report-not-ready";
}
