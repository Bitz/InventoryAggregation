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
    [Trait("Scenario", "service-telemetry/Healthy refresh")]
    public async Task A_scheduled_refresh_is_one_trace_with_the_collection_beneath_it()
    {
        var activities = new List<Activity>();
        await using var api = new TestApi { Activities = activities, PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };
        await api.StartedAsync();

        await api.NextScheduledRefreshAsync();
        var trace = TraceOf(api, activities, "RunManifest");

        var root = Assert.Single(trace, a => a.DisplayName == "refresh inventory report");
        Assert.Equal("scheduled", root.GetTagItem("inventory.trigger"));
        Assert.Equal(default, root.ParentSpanId);
        var collect = Assert.Single(trace, a => a.DisplayName == "collect inventory");
        Assert.Equal(root.SpanId, collect.ParentSpanId);
        Assert.Equal("success", collect.GetTagItem("inventory.outcome"));
        Assert.Equal(7, collect.GetTagItem("inventory.pages"));
        Assert.Equal(8, collect.GetTagItem("inventory.requests"));
        Assert.DoesNotContain(trace, a => a.Kind == ActivityKind.Server);
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Failed refresh")]
    public async Task A_failed_refresh_marks_the_span_and_warns_that_the_previous_report_is_kept()
    {
        var activities = new List<Activity>();
        var failing = false;
        var source = new FakePageSource(page =>
        {
            var result = FakePageSource.Healthy(page);
            return Volatile.Read(ref failing) && page == 3
                ? PageResult.Success(result.Page! with { Records = [FakePageSource.Healthy(2).Page!.Records[^1], .. result.Page!.Records.Take(24)] }, 1)
                : result;
        });
        await using var api = new TestApi { Activities = activities, PageSource = source };
        await api.StartedAsync();
        Volatile.Write(ref failing, true);

        await api.NextScheduledRefreshAsync();
        var trace = TraceOf(api, activities, "RunManifestWithoutData");

        var collect = Assert.Single(trace, a => a.DisplayName == "collect inventory");
        Assert.Equal(ActivityStatusCode.Error, collect.Status);
        Assert.Equal(FailureKinds.IncompleteDataset, collect.GetTagItem("inventory.outcome"));
        var warning = Assert.Single(api.Logs.GetSnapshot(), r => r.Level == Microsoft.Extensions.Logging.LogLevel.Warning && r.Message.StartsWith("Refresh (", StringComparison.Ordinal));
        Assert.Contains(FailureKinds.IncompleteDataset, warning.Message, StringComparison.Ordinal);
        Assert.Contains("previous report", warning.Message, StringComparison.Ordinal);
        Assert.Contains("is kept", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Forced request")]
    [Trait("Scenario", "service-telemetry/Forced request with a new report")]
    public async Task A_forced_request_links_its_span_to_the_refresh_it_waited_for()
    {
        var activities = new List<Activity>();
        await using var api = new TestApi { Activities = activities, PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };
        await api.StartedAsync();
        await api.ParkThenAdvanceAsync(TimeSpan.FromMinutes(2));

        var traceId = ActivityTraceId.CreateRandom();
        using var response = await SendTracedAsync(api, traceId, $"{Route}?fresh=true");

        // The server span ends just after the response has been sent.
        await FakeClock.Until(() => Snapshot(activities).Any(a => a.TraceId == traceId && a.Kind == ActivityKind.Server), "the server span to end");
        var all = Snapshot(activities);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var server = Assert.Single(all, a => a.TraceId == traceId && a.Kind == ActivityKind.Server);
        var link = Assert.Single(server.Links);
        var refresh = Assert.Single(all, a => a.SpanId == link.Context.SpanId && a.TraceId == link.Context.TraceId);
        Assert.Equal("refresh inventory report", refresh.DisplayName);
        Assert.Equal("fresh", refresh.GetTagItem("inventory.trigger"));
        Assert.NotEqual(traceId, refresh.TraceId);
        Assert.DoesNotContain(all, a => a.TraceId == traceId && a.DisplayName == "collect inventory");
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Plain request")]
    public async Task A_plain_request_links_to_the_refresh_that_produced_the_report()
    {
        var activities = new List<Activity>();
        await using var api = new TestApi { Activities = activities, PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };
        await api.StartedAsync();
        var refreshTrace = ManifestTraceId(api, "RunManifest");

        var server = await ServerSpanOfAsync(api, activities, Route);

        var link = Assert.Single(server.Links);
        Assert.Equal(refreshTrace, link.Context.TraceId.ToHexString());
        var refresh = Assert.Single(Snapshot(activities), a => a.SpanId == link.Context.SpanId && a.TraceId == link.Context.TraceId);
        Assert.Equal("refresh inventory report", refresh.DisplayName);
        Assert.Equal("startup", refresh.GetTagItem("inventory.trigger"));
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Stale report")]
    public async Task A_stale_report_links_to_the_good_refresh_not_the_failed_one()
    {
        var activities = new List<Activity>();
        var failing = false;
        var source = new FakePageSource(page => Volatile.Read(ref failing)
            ? FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)
            : FakePageSource.Healthy(page));
        await using var api = new TestApi { Activities = activities, PageSource = source };
        await api.StartedAsync();
        var goodTrace = ManifestTraceId(api, "RunManifest");
        Volatile.Write(ref failing, true);
        await api.NextScheduledRefreshAsync();
        var failedTrace = ManifestTraceId(api, "RunManifestWithoutData");

        var server = await ServerSpanOfAsync(api, activities, Route);

        var link = Assert.Single(server.Links);
        Assert.Equal(goodTrace, link.Context.TraceId.ToHexString());
        Assert.NotEqual(failedTrace, goodTrace);
    }

    [Fact]
    public async Task A_report_not_ready_response_has_no_link()
    {
        var activities = new List<Activity>();
        await using var api = new TestApi { Activities = activities, PageSource = new FakePageSource(page => FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)) };
        await api.StartedAsync();

        var server = await ServerSpanOfAsync(api, activities, Route);

        Assert.Empty(server.Links);
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
    public async Task Collections_are_counted_by_outcome_and_trigger()
    {
        var failing = false;
        var source = new FakePageSource(page => Volatile.Read(ref failing)
            ? FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)
            : FakePageSource.Healthy(page));
        MetricCollector<long>? collections = null;
        await using var api = new TestApi
        {
            PageSource = source,
            BeforeStart = services => collections = new MetricCollector<long>(services.GetRequiredService<IMeterFactory>(), InventoryTelemetry.Name, "inventory.collections"),
        };
        await api.StartedAsync();
        Volatile.Write(ref failing, true);

        await api.NextScheduledRefreshAsync();

        using (collections)
        {
            var outcomes = collections!.GetMeasurementSnapshot().Select(m => ((string)m.Tags["inventory.outcome"]!, (string)m.Tags["inventory.trigger"]!)).ToList();
            Assert.Equal([("success", "startup"), (FailureKinds.UpstreamUnavailable, "scheduled")], outcomes);
        }
    }

    [Fact]
    [Trait("Scenario", "service-telemetry/Report age")]
    public async Task The_stored_report_age_is_observed_in_seconds()
    {
        MetricCollector<double>? age = null;
        await using var api = new TestApi
        {
            PageSource = new FakePageSource(page => FakePageSource.Healthy(page)),
            BeforeStart = services => age = new MetricCollector<double>(services.GetRequiredService<IMeterFactory>(), InventoryTelemetry.Name, "inventory.report.age"),
        };
        await api.StartedAsync();

        await api.ParkThenAdvanceAsync(TimeSpan.FromSeconds(90));

        using (age)
        {
            age!.RecordObservableInstruments();
            Assert.Equal(90d, age.LastMeasurement?.Value);
        }
    }

    [Fact]
    public async Task No_age_is_observed_while_no_report_is_stored()
    {
        MetricCollector<double>? age = null;
        await using var api = new TestApi
        {
            PageSource = new FakePageSource(page => FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)),
            BeforeStart = services => age = new MetricCollector<double>(services.GetRequiredService<IMeterFactory>(), InventoryTelemetry.Name, "inventory.report.age"),
        };
        await api.StartedAsync();

        using (age)
        {
            age!.RecordObservableInstruments();
            Assert.Empty(age.GetMeasurementSnapshot());
        }
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
        var client = await api.StartedAsync();

        using var response = await client.GetAsync(Route, Ct);

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

    /// <summary>
    /// The spans of the latest refresh of this host whose manifest has event <paramref name="manifest"/>.
    /// Every host in the test run listens to the service's activity source, so spans are matched by the
    /// trace id that this host's own manifest names.
    /// </summary>
    private static List<Activity> TraceOf(TestApi api, List<Activity> activities, string manifest)
    {
        api.Services.GetRequiredService<TracerProvider>().ForceFlush();
        var record = api.Logs.GetSnapshot().Last(r => r.Id.Name == manifest);
        var traceId = record.GetStructuredStateValue("TraceId");
        return [.. Snapshot(activities).Where(a => a.TraceId.ToHexString() == traceId)];
    }

    private static string ManifestTraceId(TestApi api, string manifest) =>
        api.Logs.GetSnapshot().Last(r => r.Id.Name == manifest).GetStructuredStateValue("TraceId")!;

    /// <summary>Sends a request inside a new trace and returns its server span once it has ended.</summary>
    private static async Task<Activity> ServerSpanOfAsync(TestApi api, List<Activity> activities, string path)
    {
        var traceId = ActivityTraceId.CreateRandom();
        using var response = await SendTracedAsync(api, traceId, path);
        await FakeClock.Until(() => Snapshot(activities).Any(a => a.TraceId == traceId && a.Kind == ActivityKind.Server), "the server span to end");
        return Snapshot(activities).Single(a => a.TraceId == traceId && a.Kind == ActivityKind.Server);
    }

    /// <summary>A copy of the exported spans; other hosts' spans may be appended meanwhile.</summary>
    private static List<Activity> Snapshot(List<Activity> activities)
    {
        while (true)
        {
            try
            {
                return [.. activities];
            }
            catch (InvalidOperationException)
            {
                // Appended to while copying; try again.
            }
            catch (ArgumentException)
            {
                // Appended to while copying; try again.
            }
        }
    }

    /// <summary>Sends a report request inside a known trace, so spans from tests running in parallel can be told apart.</summary>
    private static Task<HttpResponseMessage> SendTracedAsync(TestApi api, ActivityTraceId traceId, string path = Route)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom()}-01");
        return api.CreateClient().SendAsync(request, Ct);
    }
}
