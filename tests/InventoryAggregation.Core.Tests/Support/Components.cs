using InventoryAggregation.Core.Aggregation;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Reporting;
using InventoryAggregation.Core.Telemetry;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace InventoryAggregation.Core.Tests.Support;

/// <summary>Builds Core components the way the host wires them, around a scripted page source.</summary>
internal static class Components
{
    public static InventoryCollector Collector(
        IInventoryPageSource source,
        InventoryReportSettings? settings = null,
        TimeProvider? time = null) =>
        new(source, settings ?? new InventoryReportSettings(), time ?? new FakeTimeProvider(), Metrics(), NullLogger<InventoryCollector>.Instance);

    public static InventoryMetrics Metrics() =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider().GetRequiredService<IMeterFactory>());

    public static ServiceProvider ReportServices(
        IInventoryPageSource source,
        InventoryReportSettings? settings = null,
        TimeProvider? time = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(settings ?? new InventoryReportSettings());
        services.AddSingleton(time ?? new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 15, 4, 5, TimeSpan.Zero)));
        services.AddSingleton(source);
        services.AddMetrics();
        services.AddSingleton<InventoryMetrics>();
        services.AddSingleton<RecordValidator>();
        services.AddSingleton<CategoryAggregator>();
        services.AddTransient<InventoryCollector>();
        services.AddSingleton<InventoryReportService>();
        services.AddSingleton<ReportStore>();
        services.AddSingleton<ReportRefresher>();
        return services.BuildServiceProvider();
    }
}
