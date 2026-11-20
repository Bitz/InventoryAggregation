using System.Globalization;
using System.Text.Json;
using InventoryAggregation.Api.Configuration;
using InventoryAggregation.Core;
using InventoryAggregation.Core.Collection;
using Microsoft.Extensions.Options;
using Polly.Timeout;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// Fetches one page from the upstream Inventory API and classifies the outcome (inventory-source).
/// A typed client: its <see cref="HttpClient"/> comes from <c>IHttpClientFactory</c>, with the
/// resilience pipeline and the shared rate limiter in its handler chain. Knows HTTP, status codes,
/// the envelope and the key header; knows nothing about paging strategy or totals.
/// </summary>
public sealed partial class InventoryApiClient(
    HttpClient httpClient,
    IOptions<InventoryApiOptions> options,
    TimeProvider timeProvider,
    ILogger<InventoryApiClient> logger) : IInventoryPageSource
{
    public const string HttpClientName = "InventoryApi";

    public async Task<PageResult> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var attempts = new UpstreamAttempts { Page = page };

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            string.Create(CultureInfo.InvariantCulture, $"api/records?page={page}&pageSize={pageSize}"));
        request.Headers.Add(settings.ApiKeyHeader, settings.ApiKey);
        request.Options.Set(UpstreamAttempts.Key, attempts);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutRejectedException)
        {
            return Failed(FailureKinds.UpstreamUnavailable, page, attempts, $"Page {page} did not complete within the time allowed (attempt timeout {Seconds(settings.AttemptTimeout)}, page timeout {Seconds(settings.PageTimeout)}).");
        }
        catch (HttpRequestException exception)
        {
            // The exception message can name the upstream host; only its category is passed on.
            return Failed(FailureKinds.UpstreamUnavailable, page, attempts, $"The Inventory API could not be reached for page {page} ({exception.HttpRequestError}).");
        }
        catch (OperationCanceledException)
        {
            return Failed(FailureKinds.UpstreamUnavailable, page, attempts, $"The request for page {page} was abandoned before it completed.");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (status == 200)
            {
                return await ParseAsync(response, page, attempts, cancellationToken).ConfigureAwait(false);
            }

            await LogUpstreamErrorAsync(response, page, attempts, cancellationToken).ConfigureAwait(false);
            return Classify(response, page, attempts, settings);
        }
    }

    private static async Task<PageResult> ParseAsync(HttpResponseMessage response, int page, UpstreamAttempts attempts, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (InventoryPageParser.TryParse(document.RootElement, out var parsed, out var problem))
                {
                    return PageResult.Success(parsed!, Math.Max(attempts.Count, 1));
                }

                return Failed(FailureKinds.MalformedResponse, page, attempts, $"Page {page} could not be read: {problem}.", 200);
            }
        }
        catch (JsonException)
        {
            return Failed(FailureKinds.MalformedResponse, page, attempts, $"Page {page} could not be read: the body is not valid JSON.", 200);
        }
    }

    private PageResult Classify(HttpResponseMessage response, int page, UpstreamAttempts attempts, InventoryApiOptions settings)
    {
        var status = (int)response.StatusCode;
        var retryAfter = UpstreamResilience.RetryAfter(response, timeProvider);
        var tries = Math.Max(attempts.Count, 1);

        switch (status)
        {
            case 429:
            case 503 when retryAfter is not null:
                var wait = retryAfter ?? settings.RateLimitWindow;
                var reason = retryAfter > settings.MaxRetryAfter
                    ? $"asked to wait {Seconds(wait)}, longer than the {Seconds(settings.MaxRetryAfter)} the service waits"
                    : $"still throttling after {tries} attempt(s)";
                return PageResult.Failed(new InventoryFailure(
                    FailureKinds.UpstreamThrottled,
                    $"The Inventory API is throttling requests (status {status}) for page {page}: {reason}.")
                {
                    Page = page,
                    StatusCode = status,
                    Attempts = tries,
                    RetryAfter = wait,
                });

            case 401 or 403:
                return Failed(FailureKinds.UpstreamMisconfigured, page, attempts, $"The Inventory API answered {status}: the API key is missing or rejected. Check {InventoryApiOptions.SectionName}:ApiKey.", status);

            case 400 or 404:
                return Failed(FailureKinds.UpstreamMisconfigured, page, attempts, $"The Inventory API answered {status} for page {page}: check {InventoryApiOptions.SectionName}:BaseUrl and {InventoryApiOptions.SectionName}:PageSize.", status);

            case >= 400 and < 500 when !UpstreamResilience.IsTransient(status):
                return Failed(FailureKinds.UpstreamRejected, page, attempts, $"The Inventory API rejected the request for page {page} with status {status}.", status);

            case >= 400 when UpstreamResilience.IsTransient(status):
                return Failed(FailureKinds.UpstreamUnavailable, page, attempts, $"The Inventory API answered {status} for page {page} on all {tries} attempt(s).", status);

            default:
                return Failed(FailureKinds.UpstreamUnexpectedStatus, page, attempts, $"The Inventory API answered page {page} with status {status}, which the service does not handle.", status);
        }
    }

    private async Task LogUpstreamErrorAsync(HttpResponseMessage response, int page, UpstreamAttempts attempts, CancellationToken cancellationToken)
    {
        if (!logger.IsEnabled(LogLevel.Warning))
        {
            return;
        }

        // The upstream's Problem Details carry a traceId worth keeping for correlation. Nothing else
        // from its body is logged.
        string? upstreamTraceId = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("traceId", out var traceId)
                && traceId.ValueKind == JsonValueKind.String)
            {
                upstreamTraceId = traceId.GetString();
            }
        }
        catch (JsonException)
        {
        }

        LogUpstreamError((int)response.StatusCode, page, Math.Max(attempts.Count, 1), upstreamTraceId ?? "none");
    }

    private static PageResult Failed(string kind, int page, UpstreamAttempts attempts, string detail, int? status = null) =>
        PageResult.Failed(new InventoryFailure(kind, detail)
        {
            Page = page,
            StatusCode = status ?? attempts.LastStatus,
            Attempts = attempts.Count,
        });

    private static string Seconds(TimeSpan value) => string.Create(CultureInfo.InvariantCulture, $"{value.TotalSeconds:0.###} s");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inventory API answered {Status} for page {Page} after {Attempts} attempt(s) (upstream traceId {UpstreamTraceId})")]
    private partial void LogUpstreamError(int status, int page, int attempts, string upstreamTraceId);
}
