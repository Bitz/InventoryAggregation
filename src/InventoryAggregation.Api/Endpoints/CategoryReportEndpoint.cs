using System.ComponentModel;
using System.Globalization;
using InventoryAggregation.Api.Configuration;
using InventoryAggregation.Core.Reporting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api.Endpoints;

/// <summary>
/// <c>GET /api/inventory/categories</c>. Reads the stored report, or asks the refresher for a live one
/// with <c>fresh=true</c>, maps it to the response contract with its freshness, and does nothing else.
/// </summary>
public static partial class CategoryReportEndpoint
{
    public const string Route = "/api/inventory/categories";

    public static IEndpointRouteBuilder MapCategoryReport(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, GetReportAsync)
            .WithName("GetCategoryReport")
            .WithSummary("Inventory figures per product category")
            .WithDescription(
                "Returns per-category figures from the latest report the service has proven complete and consistent. " +
                "Reports are refreshed in the background; 'freshness' and the Age header say how old the figures are " +
                "and whether the latest refresh failed. Never returns partial figures. With fresh=true the caller waits " +
                "for a live collection unless the stored report is very recent. The only failure for an upstream problem " +
                "is 503 with kind 'report-not-ready', while no good report has been produced since the service started.")
            .Produces<CategoryReportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> GetReportAsync(
        ReportRefresher refresher,
        IOptions<InventoryApiOptions> options,
        TimeProvider time,
        HttpContext context,
        [FromQuery, Description("true to wait for a live collection, unless the stored report is younger than FreshMaxAge (30 s by default).")] bool? fresh,
        [FromQuery, Description("Comma-separated extras; 'audit' adds the lineage of the run that produced the served report.")] string? include,
        [FromServices] ILoggerFactory loggerFactory)
    {
        RefreshStatus status;
        if (fresh == true)
        {
            try
            {
                status = await refresher.GetFreshAsync(context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // The caller left; the collection carries on for everyone else (design.md D33). Not an error.
                var logger = loggerFactory.CreateLogger(typeof(CategoryReportEndpoint));
                LogCallerLeft(logger);
                return Results.Empty;
            }
        }
        else
        {
            status = refresher.Status();
        }

        var now = time.GetUtcNow();
        var settings = options.Value;
        if (status.Store.Latest is not { } stored)
        {
            return FailureResponses.NotReady(status, now, settings.RateLimitWindow, context);
        }

        var response = CategoryReportResponse.From(stored, status, now, settings.StaleAfter, IncludesAudit(include));
        context.Response.Headers.Age = response.Freshness.AgeSeconds.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Ok(response);
    }

    /// <summary><c>include</c> is a comma-separated list; <c>audit</c> adds the run's lineage. Other values are ignored.</summary>
    private static bool IncludesAudit(string? include) =>
        include?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains("audit", StringComparer.OrdinalIgnoreCase) == true;

    [LoggerMessage(Level = LogLevel.Information, Message = "The caller disconnected before the live report was ready; the collection continues")]
    private static partial void LogCallerLeft(ILogger logger);
}
