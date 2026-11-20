using System.Net.Http.Headers;
using InventoryAggregation.Api.Configuration;
using InventoryAggregation.Core.Telemetry;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.RateLimiting;
using Polly.Retry;
using Polly.Timeout;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// The HTTP-layer resilience for one page fetch (design.md §2):
/// page timeout ▸ retry ▸ shared rate limiter ▸ attempt timeout.
/// The limiter sits inside the retry so every retry waits for a permit, and outside the attempt
/// timeout so waiting for a permit never counts as a slow attempt.
/// </summary>
internal static partial class UpstreamResilience
{
    public const string PipelineName = "inventory-api";

    private static readonly int[] TransientStatuses = [408, 429, 500, 502, 503, 504];

    public static void Configure(ResiliencePipelineBuilder<HttpResponseMessage> builder, ResilienceHandlerContext context)
    {
        var services = context.ServiceProvider;
        var options = services.GetRequiredService<IOptions<InventoryApiOptions>>().Value;
        var time = services.GetRequiredService<TimeProvider>();
        var limiter = services.GetRequiredService<UpstreamRateLimiter>();
        var logger = services.GetRequiredService<ILogger<InventoryApiClient>>();
        var metrics = services.GetRequiredService<InventoryMetrics>();

        builder.TimeProvider = time;

        builder.AddTimeout(new TimeoutStrategyOptions { Name = "page-timeout", Timeout = options.PageTimeout });

        if (options.MaxRetries > 0)
        {
            builder.AddRetry(new HttpRetryStrategyOptions
            {
                Name = "retry",
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                Delay = options.BaseDelay,
                UseJitter = true,
                ShouldRetryAfterHeader = false,
                ShouldHandle = args => ValueTask.FromResult(IsRetryable(args.Outcome, options.MaxRetryAfter, time)),
                DelayGenerator = args => ValueTask.FromResult(RetryAfter(args.Outcome.Result, time)),
                OnRetry = args =>
                {
                    var reason = Describe(args.Outcome);
                    LogRetry(logger, PageOf(args.Context), args.AttemptNumber + 1, reason, args.RetryDelay);
                    metrics.Retried(reason);
                    return default;
                },
            });
        }

        builder.AddRateLimiter(new RateLimiterStrategyOptions
        {
            Name = "pacing",
            RateLimiter = args => limiter.AcquireAsync(1, args.Context.CancellationToken),
        });

        builder.AddTimeout(new TimeoutStrategyOptions { Name = "attempt-timeout", Timeout = options.AttemptTimeout });
    }

    /// <summary>Transport errors, attempt timeouts and transient statuses are retried, unless the upstream asked us to wait longer than we will.</summary>
    internal static bool IsRetryable(Outcome<HttpResponseMessage> outcome, TimeSpan maxRetryAfter, TimeProvider time) => outcome switch
    {
        { Exception: HttpRequestException or TimeoutRejectedException } => true,
        { Result: { } response } when IsTransient((int)response.StatusCode) =>
            RetryAfter(response, time) is not { } wait || wait <= maxRetryAfter,
        _ => false,
    };

    internal static bool IsTransient(int status) => TransientStatuses.Contains(status);

    /// <summary>The wait a response asks for in <c>Retry-After</c> (seconds or HTTP-date), if any.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage? response, TimeProvider time)
    {
        if (response?.Headers.RetryAfter is not RetryConditionHeaderValue header)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header.Date is { } date)
        {
            var wait = date - time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    private static int? PageOf(ResilienceContext context) =>
        context.GetRequestMessage()?.Options.TryGetValue(UpstreamAttempts.Key, out var attempts) == true ? attempts.Page : null;

    private static string Describe(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception is { } exception
            ? exception.GetType().Name
            : $"status {(int)outcome.Result!.StatusCode}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Page {Page} attempt {Attempt} failed with {Outcome}; retrying in {Delay}")]
    private static partial void LogRetry(ILogger logger, int? page, int attempt, string outcome, TimeSpan delay);
}
