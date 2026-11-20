using InventoryAggregation.Core.Reporting;
using Microsoft.AspNetCore.Mvc;

namespace InventoryAggregation.Api.Endpoints;

/// <summary>
/// <c>GET /api/inventory/categories</c>. Calls the report service, maps success to the response
/// contract and failure to Problem Details, and does nothing else.
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
                "Collects every record from the Inventory API, verifies the dataset is complete and consistent, " +
                "and returns per-category figures. Never returns partial figures: when completeness cannot be " +
                "proven the response is Problem Details whose 'kind' names the reason.")
            .Produces<CategoryReportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        return endpoints;
    }

    private static async Task<IResult> GetReportAsync(
        InventoryReportService reports,
        HttpContext context,
        [FromQuery] string? include,
        [FromServices] ILoggerFactory loggerFactory)
    {
        try
        {
            var result = await reports.GetReportAsync(context.RequestAborted);
            return result.IsSuccess
                ? TypedResults.Ok(CategoryReportResponse.From(result.Report, IncludesAudit(include)))
                : FailureResponses.ToResult(result.Failure, context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller left; nobody is waiting for an answer. Not an error.
            var logger = loggerFactory.CreateLogger(typeof(CategoryReportEndpoint));
            LogCallerLeft(logger);
            return Results.Empty;
        }
    }

    /// <summary><c>include</c> is a comma-separated list; <c>audit</c> adds the run's lineage. Other values are ignored.</summary>
    private static bool IncludesAudit(string? include) =>
        include?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains("audit", StringComparer.OrdinalIgnoreCase) == true;

    [LoggerMessage(Level = LogLevel.Information, Message = "The caller disconnected before the report was ready")]
    private static partial void LogCallerLeft(ILogger logger);
}
