using System.Diagnostics;
using System.Net;
using InventoryAggregation.Api.Configuration;
using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Core.Aggregation;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Reporting;
using InventoryAggregation.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace InventoryAggregation.Api;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the service's own components: validated options, the typed upstream client with its
    /// resilience pipeline and shared rate limiter, the collector, validator, aggregator, report
    /// service, and <see cref="TimeProvider.System"/> unless a clock is already registered.
    /// </summary>
    public static IServiceCollection AddInventoryAggregation(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<InventoryApiOptions>()
            .Bind(configuration.GetSection(InventoryApiOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<InventoryApiOptions>, InventoryApiOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<InventoryApiOptions>>().Value.ToReportSettings());

        services.AddMetrics();
        services.AddSingleton<InventoryMetrics>();
        services.AddSingleton<RecordValidator>();
        services.AddSingleton<CategoryAggregator>();
        services.AddTransient<InventoryCollector>();
        services.AddSingleton<InventoryReportService>();

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<InventoryApiOptions>>().Value;
            return new UpstreamRateLimiter(options.RateLimitPermits, options.RateLimitWindow, sp.GetRequiredService<TimeProvider>());
        });
        services.AddTransient<UpstreamAttemptHandler>();

        services
            .AddHttpClient<IInventoryPageSource, InventoryApiClient>(InventoryApiClient.HttpClientName, (sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<InventoryApiOptions>>().Value;
                client.BaseAddress = WithTrailingSlash(options.BaseUrl!);

                // The resilience pipeline bounds every attempt and every page; the client adds no timeout of its own.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),

                // Send only what the request needs: no trace-context headers.
                ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
            })

            // The default request logging writes the full upstream URL, which names the upstream host.
            // Our own logs name the page instead. Header values are redacted should logging be re-enabled.
            .RemoveAllLoggers()
            .RedactLoggedHeaders(_ => true)
            .AddResilienceHandler(UpstreamResilience.PipelineName, UpstreamResilience.Configure)
            .Services.AddHttpClient(InventoryApiClient.HttpClientName)
            .AddHttpMessageHandler<UpstreamAttemptHandler>();

        // Polly's own telemetry logs exception messages, which can name the upstream host. The client
        // logs every retry itself with the page, attempt, outcome and delay.
        services.AddLogging(logging => logging.AddFilter("Polly", LogLevel.None));

        return services;
    }

    private static Uri WithTrailingSlash(string baseUrl) =>
        new(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute);
}
