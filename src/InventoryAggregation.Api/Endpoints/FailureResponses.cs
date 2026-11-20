using System.Diagnostics;
using System.Globalization;
using InventoryAggregation.Core;
using InventoryAggregation.Core.Reporting;
using Microsoft.AspNetCore.Mvc;

namespace InventoryAggregation.Api.Endpoints;

/// <summary>
/// The one place that maps a failure kind to an HTTP status and a Problem Details body (design.md D16).
/// Collection failures no longer reach the caller as errors; they show as
/// <c>freshness.lastRefresh.outcome</c>. What is left is <c>report-not-ready</c> while no good report
/// exists, and <c>internal-error</c> for a bug.
/// </summary>
public static class FailureResponses
{
    private static readonly Dictionary<string, (int Status, string Title)> ByKind = new(StringComparer.Ordinal)
    {
        [FailureKinds.ReportNotReady] = (StatusCodes.Status503ServiceUnavailable, "No inventory report is available yet"),
        [FailureKinds.InternalError] = (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
    };

    public static IReadOnlyCollection<string> Kinds => ByKind.Keys;

    public static int StatusFor(string kind) =>
        ByKind.TryGetValue(kind, out var entry) ? entry.Status : StatusCodes.Status500InternalServerError;

    public static ProblemDetails Create(string kind, string detail, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var (status, title) = ByKind.TryGetValue(kind, out var entry) ? entry : (StatusCodes.Status500InternalServerError, "An unexpected error occurred");

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

    /// <summary>
    /// <c>503 report-not-ready</c> (design.md D29) with the last attempt, when one has finished, and
    /// <c>Retry-After</c>: <paramref name="rateLimitWindow"/> while a refresh is running, otherwise the
    /// seconds until the next attempt plus <paramref name="rateLimitWindow"/>.
    /// </summary>
    public static IResult NotReady(RefreshStatus status, DateTimeOffset now, TimeSpan rateLimitWindow, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(context);

        var attempt = status.Store.LastAttempt;
        var detail = (status.IsRefreshing, attempt) switch
        {
            (true, null) => "The first report since the service started is being collected.",
            (true, _) => $"No report has been produced since the service started; the last attempt failed with {attempt.Outcome} and another is running.",
            (false, null) => "No report has been produced since the service started.",
            (false, _) => $"No report has been produced since the service started; the last attempt failed with {attempt.Outcome}.",
        };

        var problem = Create(FailureKinds.ReportNotReady, detail, context);
        if (attempt is not null)
        {
            problem.Extensions["lastRefresh"] = LastRefreshResponse.From(attempt);
        }

        var untilNext = status.NextRefreshAt - now;
        var wait = status.IsRefreshing || untilNext <= TimeSpan.Zero ? rateLimitWindow : untilNext + rateLimitWindow;
        context.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        return TypedResults.Problem(problem);
    }
}
