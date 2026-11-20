using System.Diagnostics;
using System.Globalization;
using InventoryAggregation.Core;
using Microsoft.AspNetCore.Mvc;

namespace InventoryAggregation.Api.Endpoints;

/// <summary>
/// The one place that maps a failure kind to an HTTP status and a Problem Details body (design.md D16).
/// The status tells the caller whose fault it is and whether retrying helps.
/// </summary>
public static class FailureResponses
{
    private static readonly Dictionary<string, (int Status, string Title)> ByKind = new(StringComparer.Ordinal)
    {
        [FailureKinds.UpstreamUnavailable] = (StatusCodes.Status502BadGateway, "The Inventory API could not be reached"),
        [FailureKinds.UpstreamThrottled] = (StatusCodes.Status503ServiceUnavailable, "The Inventory API is throttling requests"),
        [FailureKinds.UpstreamTimeout] = (StatusCodes.Status504GatewayTimeout, "The report did not finish in time"),
        [FailureKinds.UpstreamMisconfigured] = (StatusCodes.Status500InternalServerError, "The service's Inventory API configuration was rejected"),
        [FailureKinds.UpstreamRejected] = (StatusCodes.Status502BadGateway, "The Inventory API rejected the request"),
        [FailureKinds.UpstreamUnexpectedStatus] = (StatusCodes.Status502BadGateway, "The Inventory API answered with an unexpected status"),
        [FailureKinds.MalformedResponse] = (StatusCodes.Status502BadGateway, "The Inventory API sent a response that could not be read"),
        [FailureKinds.InconsistentPaging] = (StatusCodes.Status502BadGateway, "The Inventory API's pages do not agree"),
        [FailureKinds.IncompleteDataset] = (StatusCodes.Status502BadGateway, "The upstream data could not be verified as complete"),
        [FailureKinds.ConflictingRecords] = (StatusCodes.Status502BadGateway, "The Inventory API returned conflicting records"),
        [FailureKinds.InvalidRecords] = (StatusCodes.Status502BadGateway, "Some inventory records are invalid"),
        [FailureKinds.DatasetTooLarge] = (StatusCodes.Status502BadGateway, "The Inventory API reports more data than the service accepts"),
        [FailureKinds.InternalError] = (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
    };

    public static IReadOnlyCollection<string> Kinds => ByKind.Keys;

    public static int StatusFor(string kind) =>
        ByKind.TryGetValue(kind, out var entry) ? entry.Status : StatusCodes.Status502BadGateway;

    public static ProblemDetails Create(string kind, string detail, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (status, title) = ByKind.TryGetValue(kind, out var entry) ? entry : (StatusCodes.Status502BadGateway, "The report could not be produced");

        var problem = new ProblemDetails
        {
            Type = $"urn:inventory-aggregation:{kind}",
            Title = title,
            Status = status,
            Detail = detail,
        };
        problem.Extensions["kind"] = kind;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        return problem;
    }

    /// <summary>The Problem Details result for a failure, with its evidence and, for throttling, <c>Retry-After</c>.</summary>
    public static IResult ToResult(InventoryFailure failure, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(context);

        var problem = Create(failure.Kind, failure.Detail, context);
        AddIfPresent(problem, "page", failure.Page);
        AddIfPresent(problem, "expectedCount", failure.ExpectedCount);
        AddIfPresent(problem, "collectedCount", failure.CollectedCount);
        if (failure.DuplicateIds.Count > 0)
        {
            problem.Extensions["duplicateIds"] = failure.DuplicateIds;
        }

        if (failure.ConflictingIds.Count > 0)
        {
            problem.Extensions["conflictingIds"] = failure.ConflictingIds;
        }

        if (failure.RejectedRecords.Count > 0)
        {
            problem.Extensions["rejectedRecords"] = failure.RejectedRecords.Select(RejectedRecordResponse.From).ToList();
        }

        if (problem.Status == StatusCodes.Status503ServiceUnavailable)
        {
            var wait = failure.RetryAfter ?? TimeSpan.FromSeconds(10);
            context.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Problem(problem);
    }

    private static void AddIfPresent(ProblemDetails problem, string name, int? value)
    {
        if (value is not null)
        {
            problem.Extensions[name] = value;
        }
    }
}
