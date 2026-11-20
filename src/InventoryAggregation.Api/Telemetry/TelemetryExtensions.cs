using System.Diagnostics;
using InventoryAggregation.Core.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace InventoryAggregation.Api.Telemetry;

/// <summary>
/// Logs, traces and metrics through OpenTelemetry, exported over OTLP only when
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set (design.md D25). Endpoint, protocol, headers and service
/// name come from the standard <c>OTEL_*</c> settings.
/// </summary>
public static class TelemetryExtensions
{
    public const string EndpointSetting = "OTEL_EXPORTER_OTLP_ENDPOINT";
    public const string DefaultServiceName = "inventory-aggregation";

    /// <summary>The runtime's own source for outgoing HTTP requests; every one of ours goes to the upstream.</summary>
    internal const string HttpClientSource = "System.Net.Http";

    public static bool ExportEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration?[EndpointSetting]);

    public static IServiceCollection AddInventoryTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(resource =>
            {
                // OTEL_SERVICE_NAME, when set, is applied by the default resource detectors.
                if (string.IsNullOrWhiteSpace(configuration["OTEL_SERVICE_NAME"]))
                {
                    resource.AddService(DefaultServiceName);
                }
            })
            .WithTracing(tracing => tracing
                .AddProcessor(new UpstreamSpanRedactor())
                .AddSource(InventoryTelemetry.Name)
                .AddSource(HttpClientSource)
                .AddAspNetCoreInstrumentation())

            // The HTTP client's own metrics are tagged with the upstream host, so they are not collected.
            .WithMetrics(metrics => metrics
                .AddMeter(InventoryTelemetry.Name)
                .AddAspNetCoreInstrumentation())
            .WithLogging(_ => { }, options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            });

        if (ExportEnabled(configuration))
        {
            telemetry.UseOtlpExporter();
        }

        services.AddHostedService<TelemetryStartupLog>();
        return services;
    }
}

/// <summary>
/// Rewrites spans of outgoing requests so that they keep the method, status, path and query, but not
/// the upstream host, port or full URL, which are treated like secrets (design.md D25). Runs when the
/// span starts and again when it ends, before any exporter sees it.
/// </summary>
internal sealed class UpstreamSpanRedactor : BaseProcessor<Activity>
{
    private static readonly string[] Removed = ["server.address", "server.port", "network.peer.address", "network.peer.port", "url.domain"];

    public override void OnStart(Activity data) => Redact(data);

    public override void OnEnd(Activity data) => Redact(data);

    internal static void Redact(Activity activity)
    {
        if (activity.Source.Name != TelemetryExtensions.HttpClientSource)
        {
            return;
        }

        if (activity.GetTagItem("url.full") is string full && Uri.TryCreate(full, UriKind.Absolute, out var url))
        {
            activity.SetTag("url.full", url.PathAndQuery);
        }

        foreach (var tag in Removed)
        {
            activity.SetTag(tag, null);
        }
    }
}

/// <summary>Says at startup whether telemetry leaves the process, without the endpoint or its headers.</summary>
internal sealed partial class TelemetryStartupLog(IConfiguration configuration, ILogger<TelemetryStartupLog> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (TelemetryExtensions.ExportEnabled(configuration))
        {
            LogExportOn(configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] ?? "grpc");
        }
        else
        {
            LogExportOff(TelemetryExtensions.EndpointSetting);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "Telemetry export is on: OTLP over {Protocol}")]
    private partial void LogExportOn(string protocol);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telemetry export is off; set {Setting} to export logs, traces and metrics over OTLP")]
    private partial void LogExportOff(string setting);
}
