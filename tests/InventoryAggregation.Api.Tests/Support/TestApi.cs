using System.Collections.Concurrent;
using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Core;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Reporting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using OpenTelemetry.Trace;

namespace InventoryAggregation.Api.Tests.Support;

/// <summary>
/// The real host, in Production unless told otherwise, with test settings, a fake clock and captured
/// logs. Either the page source is replaced by a fake, or the upstream's primary HTTP handler is
/// scripted and everything else is production code. The host's background refresh starts with the
/// host, as in production; the helpers below wait for it without waiting on real time.
/// </summary>
internal sealed class TestApi : WebApplicationFactory<Program>
{
    public string Environment { get; init; } = "Production";

    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["InventoryApi:BaseUrl"] = ClientHarness.BaseUrl,
        ["InventoryApi:ApiKey"] = ClientHarness.ApiKey,
        ["InventoryApi:RateLimitPermits"] = "100",
    };

    public IInventoryPageSource? PageSource { get; init; }

    public Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>>? Upstream { get; init; }

    public LogLevel MinimumLogLevel { get; init; } = LogLevel.Information;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 20, 15, 4, 5, TimeSpan.Zero));

    /// <summary>The host's clock: <see cref="Time"/>, with its pending timers visible.</summary>
    public TrackedClock Clock => _clock ??= new TrackedClock(Time);

    private TrackedClock? _clock;

    /// <summary>When set, finished spans are collected here as well as wherever else they go.</summary>
    public List<System.Diagnostics.Activity>? Activities { get; init; }

    /// <summary>Runs on the built host's services before the host starts, so before the startup refresh.</summary>
    public Action<IServiceProvider>? BeforeStart { get; init; }

    public ScriptedHttpHandler? UpstreamHandler { get; private set; }

    public FakeLogCollector Logs => Services.GetFakeLogCollector();

    public ReportRefresher Refresher => Services.GetRequiredService<ReportRefresher>();

    public ReportStore Store => Services.GetRequiredService<ReportStore>();

    /// <summary>Starts the host and waits until the startup refresh has finished, good or failed.</summary>
    public async Task<HttpClient> StartedAsync()
    {
        var client = CreateClient();
        await FakeClock.Until(() => Store.Snapshot.LastAttempt is not null && !Refresher.Status().IsRefreshing, "the startup refresh to finish");
        return client;
    }

    /// <summary>As <see cref="StartedAsync"/>, advancing the fake clock while the startup refresh needs it (retries, pacing).</summary>
    public async Task<HttpClient> StartedDrivingClockAsync(TimeSpan? step = null)
    {
        var client = CreateClient();
        await FakeClock.DriveUntil(Time, () => Store.Snapshot.LastAttempt is not null && !Refresher.Status().IsRefreshing, step);
        return client;
    }

    /// <summary>
    /// Moves the fake clock by <paramref name="by"/>, first waiting until the refresh schedule is parked
    /// on the timer for its next refresh, so that the timer fires where it should.
    /// </summary>
    public async Task ParkThenAdvanceAsync(TimeSpan by)
    {
        var next = Refresher.Status().NextRefreshAt;
        if (next > Time.GetUtcNow())
        {
            await FakeClock.Until(() => Clock.HasTimerDueAt(next), "the schedule to wait for its next refresh");
        }

        Time.Advance(by);
    }

    /// <summary>Moves the fake clock to the next due refresh and waits until it has finished.</summary>
    public async Task<ReportSnapshot> NextScheduledRefreshAsync()
    {
        var before = Store.Snapshot;
        var wait = Refresher.Status().NextRefreshAt - Time.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            await ParkThenAdvanceAsync(wait);
        }

        await FakeClock.Until(() => !ReferenceEquals(Store.Snapshot, before) && !Refresher.Status().IsRefreshing, "the scheduled refresh to finish");
        return Store.Snapshot;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var host = builder.Build();
        BeforeStart?.Invoke(host.Services);
        host.Start();
        return host;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(Settings));
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(MinimumLogLevel).AddFakeLogging());
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            if (Activities is not null)
            {
                services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(Activities));
            }

            if (PageSource is not null)
            {
                services.RemoveAll<IInventoryPageSource>();
                services.AddSingleton(PageSource);
            }

            if (Upstream is not null)
            {
                UpstreamHandler = new ScriptedHttpHandler(Upstream, Time);
                services.AddHttpClient(InventoryApiClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => UpstreamHandler);
            }
        });
    }
}

/// <summary>An in-memory page source for endpoint tests.</summary>
internal sealed class FakePageSource(Func<int, int, CancellationToken, Task<PageResult>> script) : IInventoryPageSource
{
    private readonly ConcurrentQueue<int> _calls = new();

    public FakePageSource(Func<int, PageResult> script)
        : this((page, _, _) => Task.FromResult(script(page)))
    {
    }

    public IReadOnlyList<int> Calls => [.. _calls];

    public Task<PageResult> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        _calls.Enqueue(page);
        return script(page, pageSize, cancellationToken);
    }

    /// <summary>157 records over pages of 25, healthy, empty past the end.</summary>
    public static PageResult Healthy(int page, int count = 157, int size = 25)
    {
        var records = Enumerable.Range(1001, count)
            .Skip((page - 1) * size)
            .Take(size)
            .Select(id => new RawRecord
            {
                Id = id,
                Name = $"Item {id}",
                Category = id % 2 == 0 ? "Toys" : " electronics ",
                Price = 1.5m,
                Quantity = id % 10 == 0 ? 0 : 2,
                LastUpdated = "2026-10-15T12:30:00Z",
            })
            .ToList();
        return PageResult.Success(new InventoryPage(page, size, (int)Math.Ceiling(count / (double)size), count, records), 1);
    }

    public static PageResult Fail(string kind, int page, int? status = null, TimeSpan? retryAfter = null) =>
        PageResult.Failed(new InventoryFailure(kind, $"Scripted {kind} on page {page}.") { Page = page, StatusCode = status, Attempts = 1, RetryAfter = retryAfter });
}
