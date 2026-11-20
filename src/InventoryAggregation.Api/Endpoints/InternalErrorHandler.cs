using InventoryAggregation.Core;
using Microsoft.AspNetCore.Diagnostics;

namespace InventoryAggregation.Api.Endpoints;

/// <summary>
/// Turns any unhandled exception into a <c>500</c> Problem Details response with
/// <c>kind: internal-error</c> and the trace id. The exception message and stack trace reach the
/// caller only in Development.
/// </summary>
internal sealed partial class InternalErrorHandler(IHostEnvironment environment, ILogger<InternalErrorHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            // The caller left; there is nobody to answer and nothing went wrong.
            return true;
        }

        LogUnhandled(exception);

        var development = environment.IsDevelopment();
        var problem = FailureResponses.Create(
            FailureKinds.InternalError,
            development ? exception.Message : "The service failed unexpectedly. Quote the traceId when reporting this.",
            httpContext);
        if (development)
        {
            problem.Extensions["exception"] = exception.ToString();
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await httpContext.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", cancellationToken);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception while producing the category report")]
    private partial void LogUnhandled(Exception exception);
}
