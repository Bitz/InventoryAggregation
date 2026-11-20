using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using InventoryAggregation.Api.Telemetry;
using InventoryAggregation.Api.Tests.Support;
using InventoryAggregation.Core;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using OpenTelemetry.Trace;

namespace InventoryAggregation.Api.Tests.Telemetry;

public sealed class TelemetryTests
{
    private const string Route = "/api/inventory/categories";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "service-telemetry/No endpoint configured")]
    public async Task Without_an_endpoint_export_is_off_and_said_so()
    {
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };

        using var response = await api.CreateClient().GetAsync(Route, Ct);

        Assert.False(TelemetryExtensions.ExportEnabled(api.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()));
        Assert.Contains(api.Logs.GetSnapshot(), r => r.Message.StartsWith("Telemetry export is off", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Endpoint configured")]
    public async Task With_an_endpoint_export_is_on_and_the_headers_stay_out_of_the_log()
    {
        const string credential = "otlp-token-4e7a";
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };
        api.Settings[TelemetryExtensions.EndpointSetting] = "http://127.0.0.1:9";
        api.Settings["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        api.Settings["OTEL_EXPORTER_OTLP_HEADERS"] = $"Authorization=Bearer {credential}";

        using var response = await api.CreateClient().GetAsync(Route, Ct);

        var logs = api.Logs.GetSnapshot();
        Assert.Contains(logs, r => r.Message == "Telemetry export is on: OTLP over http/protobuf");
        Assert.All(logs, r => Assert.DoesNotContain(credential, r.Message, StringComparison.Ordinal));
        Assert.All(logs, r => Assert.DoesNotContain("127.0.0.1:9", r.Message, StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Healthy report")]
    public async Task A_healthy_report_records_a_collect_inventory_span()
    {
        var activities = new List<Activity>();
        await using var api = new TestApi { Activities = activities, PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };

        var traceId = ActivityTraceId.CreateRandom();
        using var response = await SendTracedAsync(api, traceId);
        api.Services.GetRequiredService<TracerProvider>().ForceFlush();
        activities = [.. activities.Where(a => a.TraceId == traceId)];

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var collect = Assert.Single(activities, a => a.DisplayName == "collect inventory");
        Assert.Equal("success", collect.GetTagItem("inventory.outcome"));
        Assert.Equal(7, collect.GetTagItem("inventory.pages"));
        Assert.Equal(8, collect.GetTagItem("inventory.requests"));
        var server = Assert.Single(activities, a => a.Kind == ActivityKind.Server);
        Assert.Equal(server.TraceId, collect.TraceId);
        Assert.Equal(server.SpanId, collect.ParentSpanId);
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Failed report")]
    public async Task A_failed_report_marks_the_span_with_the_failure_kind()
    {
        var activities = new List<Activity>();
        var source = new FakePageSource(page =>
        {
            var result = FakePageSource.Healthy(page);
            return page == 3
                ? PageResult.Success(result.Page! with { Records = [FakePageSource.Healthy(2).Page!.Records[^1], .. result.Page!.Records.Take(24)] }, 1)
                : result;
        });
        await using var api = new TestApi { Activities = activities, PageSource = source };

        var traceId = ActivityTraceId.CreateRandom();
        using var response = await SendTracedAsync(api, traceId);
        api.Services.GetRequiredService<TracerProvider>().ForceFlush();
        activities = [.. activities.Where(a => a.TraceId == traceId)];

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var collect = Assert.Single(activities, a => a.DisplayName == "collect inventory");
        Assert.Equal(ActivityStatusCode.Error, collect.Status);
        Assert.Equal(FailureKinds.IncompleteDataset, collect.GetTagItem("inventory.outcome"));
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Upstream request span")]
    public void Upstream_request_spans_lose_the_host_port_and_full_url()
    {
        // A source with the runtime HTTP client's name, listened to only by this test.
        using var httpSource = new ActivitySource("System.Net.Http");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => ReferenceEquals(source, httpSource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = httpSource.StartActivity("GET", ActivityKind.Client)!;
        activity.SetTag("url.full", "https://inventory.test:8443/api/records?page=3&pageSize=25");
        activity.SetTag("server.address", "inventory.test");
        activity.SetTag("server.port", 8443);
        activity.SetTag("http.request.method", "GET");
        activity.SetTag("http.response.status_code", 200);

        new UpstreamSpanRedactor().OnEnd(activity);

        Assert.Equal("/api/records?page=3&pageSize=25", activity.GetTagItem("url.full"));
        Assert.Null(activity.GetTagItem("server.address"));
        Assert.Null(activity.GetTagItem("server.port"));
        Assert.Equal("GET", activity.GetTagItem("http.request.method"));
        Assert.Equal(200, activity.GetTagItem("http.response.status_code"));
        Assert.DoesNotContain(activity.TagObjects, t => t.Value?.ToString()?.Contains("inventory.test", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Spans_from_other_sources_are_left_alone()
    {
        using var activity = new Activity("other").SetTag("server.address", "example.test").Start();

        new UpstreamSpanRedactor().OnEnd(activity);

        Assert.Equal("example.test", activity.GetTagItem("server.address"));
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Outcomes counted")]
    public async Task Collections_are_counted_by_outcome()
    {
        var failing = false;
        var source = new FakePageSource(page => Volatile.Read(ref failing)
            ? FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)
            : FakePageSource.Healthy(page));
        await using var api = new TestApi { PageSource = source };
        var client = api.CreateClient();
        using var collections = new MetricCollector<long>(api.Services.GetRequiredService<IMeterFactory>(), InventoryTelemetry.Name, "inventory.collections");

        (await client.GetAsync(Route, Ct)).Dispose();
        Volatile.Write(ref failing, true);
        (await client.GetAsync(Route, Ct)).Dispose();

        var outcomes = collections.GetMeasurementSnapshot().Select(m => (string)m.Tags["inventory.outcome"]!).ToList();
        Assert.Equal(["success", FailureKinds.UpstreamUnavailable], outcomes);
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Retries counted")]
    public async Task Retries_are_counted()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var harness = new ClientHarness((request, n, _) => Task.FromResult(
            n < 2 ? ScriptedHttpHandler.Problem(HttpStatusCode.InternalServerError) : upstream.Respond(request)));
        using var retries = new MetricCollector<long>(harness.Services.GetRequiredService<IMeterFactory>(), InventoryTelemetry.Name, "inventory.upstream.retries");

        var result = await harness.Drive(harness.Client().GetPageAsync(2, 25, Ct));

        Assert.True(result.IsSuccess);
        var measurements = retries.GetMeasurementSnapshot();
        Assert.Equal(2, measurements.Sum(m => m.Value));
        Assert.All(measurements, m => Assert.Equal("status 500", m.Tags["inventory.retry.reason"]));
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Healthy report progress")]
    public async Task Each_page_the_end_check_and_the_outcome_are_logged_under_one_id()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var api = new TestApi { Upstream = (request, _, _) => Task.FromResult(upstream.Respond(request)) };

        using var response = await api.CreateClient().GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var lines = api.Logs.GetSnapshot()
            .Where(r => r.Level == Microsoft.Extensions.Logging.LogLevel.Information && r.Message.StartsWith("report ", StringComparison.Ordinal))
            .Select(r => r.Message)
            .ToList();
        var id = lines[0].Split(':')[0];
        Assert.All(lines, line => Assert.StartsWith(id + ":", line, StringComparison.Ordinal));
        Assert.NotEqual("report -", id);
        for (var page = 1; page <= 7; page++)
        {
            Assert.Single(lines, l => l.StartsWith($"{id}: page {page}/7: ", StringComparison.Ordinal));
        }

        Assert.Single(lines, l => l.StartsWith($"{id}: page 8 (end check): 0 records, end confirmed", StringComparison.Ordinal));
        Assert.Single(lines, l => l.StartsWith($"{id}: collected 157 records over 7 pages in 8 requests", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith($"{id}: page 2/7: 26 records (1 attempt(s), ", StringComparison.Ordinal));
        Assert.All(api.Logs.GetSnapshot(), r =>
        {
            Assert.DoesNotContain("inventory.test", r.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(ClientHarness.ApiKey, r.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Waiting for a permit")]
    public async Task A_request_that_waits_for_a_permit_says_so()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var harness = new ClientHarness(
            (request, _, _) => Task.FromResult(upstream.Respond(request)),
            new Dictionary<string, string?> { ["InventoryApi:RateLimitPermits"] = "1" });
        var client = harness.Client();

        Assert.True((await client.GetPageAsync(1, 25, Ct)).IsSuccess);
        Assert.True((await harness.Drive(client.GetPageAsync(2, 25, Ct), TimeSpan.FromSeconds(1))).IsSuccess);

        var wait = Assert.Single(harness.Logs.GetSnapshot(), r => r.Message.Contains("for a rate-limit permit", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, wait.Level);
        Assert.Matches(@"^report -: page 2 waited 1\d\.\d s for a rate-limit permit$", wait.Message);
    }

    /// <summary>Sends the report request inside a known trace, so spans from tests running in parallel can be told apart.</summary>
    private static Task<HttpResponseMessage> SendTracedAsync(TestApi api, ActivityTraceId traceId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Route);
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        return api.CreateClient().SendAsync(request, Ct);
    }
}
