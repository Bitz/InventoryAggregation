using System.Net;
using System.Net.Http.Headers;
using InventoryAggregation.Api.Tests.Support;
using InventoryAggregation.Api.Upstream;
using InventoryAggregation.Core;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Reporting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InventoryAggregation.Api.Tests.Upstream;

public sealed class InventoryApiClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "inventory-source/Request shape")]
    public async Task Sends_one_request_with_the_key_in_a_header_and_not_in_the_url()
    {
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Json(new FakeUpstream(TimeProvider.System).PageJson(3, 25))));

        var result = await harness.Client().GetPageAsync(3, 25, Ct);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(harness.Handler.Requests);
        Assert.Equal(new Uri("https://inventory.test/api/records?page=3&pageSize=25"), request.Uri);
        Assert.Equal([ClientHarness.ApiKey], request.Headers["X-Api-Key"]);
        Assert.Equal(["X-Api-Key"], request.Headers.Keys);
        Assert.DoesNotContain(ClientHarness.ApiKey, request.Uri.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_base_url_with_a_path_keeps_its_path()
    {
        await using var harness = new ClientHarness(
            (_, _, _) => Task.FromResult(ScriptedHttpHandler.Json(new FakeUpstream(TimeProvider.System).PageJson(1, 25))),
            new Dictionary<string, string?> { ["InventoryApi:BaseUrl"] = "https://inventory.test/v2" });

        await harness.Client().GetPageAsync(1, 25, Ct);

        Assert.Equal(new Uri("https://inventory.test/v2/api/records?page=1&pageSize=25"), Assert.Single(harness.Handler.Requests).Uri);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Client lifetime")]
    public async Task Reports_get_their_client_from_the_factory_and_share_its_handler()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var harness = new ClientHarness(
            (request, _, _) => Task.FromResult(upstream.Respond(request)),
            new Dictionary<string, string?> { ["InventoryApi:RateLimitPermits"] = "100" });
        var reports = harness.Services.GetRequiredService<InventoryReportService>();

        var first = await reports.GetReportAsync(Ct);
        var second = await reports.GetReportAsync(Ct);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(16, harness.Handler.Requests.Count);
        Assert.Equal(1, harness.HandlersCreated);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Client lifetime")]
    public async Task The_production_handler_chain_ends_in_a_factory_built_sockets_handler()
    {
        await using var harness = new ClientHarness(replacePrimaryHandler: false);
        var factory = harness.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        HttpMessageHandler handler = factory.CreateHandler(InventoryApiClient.HttpClientName);
        var chain = new List<string>();
        while (handler is DelegatingHandler delegating)
        {
            chain.Add(delegating.GetType().Name);
            handler = delegating.InnerHandler!;
        }

        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseCookies);
        Assert.Contains(nameof(UpstreamAttemptHandler), chain);
        Assert.DoesNotContain(chain, name => name.Contains("Logging", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Upstream unreachable")]
    public async Task Connection_errors_on_every_attempt_become_upstream_unavailable()
    {
        await using var harness = new ClientHarness((_, _, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused (inventory.test:443)"));

        var result = await harness.Drive(harness.Client().GetPageAsync(2, 25, Ct));

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.UpstreamUnavailable, result.Failure.Kind);
        Assert.Equal(2, result.Failure.Page);
        Assert.Equal(4, result.Failure.Attempts);
        Assert.DoesNotContain("inventory.test", result.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Caller cancels")]
    public async Task Caller_cancellation_aborts_the_request_and_is_not_retried()
    {
        var aborted = false;
        await using var harness = new ClientHarness(async (_, _, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                aborted = true;
                throw;
            }

            return ScriptedHttpHandler.Json("{}");
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var fetching = harness.Client().GetPageAsync(2, 25, caller.Token);
        await FakeClock.Until(() => harness.Handler.Requests.Count == 1, "the request to be sent");
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetching);
        Assert.True(aborted);
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/One server error, then success")]
    public async Task One_server_error_then_success_takes_two_attempts()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var harness = new ClientHarness((request, n, _) => Task.FromResult(
            n == 0 ? ScriptedHttpHandler.Problem(HttpStatusCode.InternalServerError) : upstream.Respond(request)));

        var result = await harness.Drive(harness.Client().GetPageAsync(2, 25, Ct));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(26, result.Page.Records.Count);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Retries exhausted")]
    public async Task Four_503s_without_retry_after_become_upstream_unavailable()
    {
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Problem(HttpStatusCode.ServiceUnavailable)));

        var result = await harness.Drive(harness.Client().GetPageAsync(2, 25, Ct));

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.UpstreamUnavailable, result.Failure.Kind);
        Assert.Equal(4, result.Failure.Attempts);
        Assert.Equal(503, result.Failure.StatusCode);
        Assert.Equal(4, harness.Handler.Requests.Count);
    }

    [Fact]
    public async Task Persistent_429s_become_upstream_throttled()
    {
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Problem(HttpStatusCode.TooManyRequests, retryAfterSeconds: 1)));

        var result = await harness.Drive(harness.Client().GetPageAsync(2, 25, Ct));

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.UpstreamThrottled, result.Failure.Kind);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Failure.RetryAfter);
        Assert.Equal(4, result.Failure.Attempts);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/A collection larger than one window")]
    public async Task Eight_requests_go_five_in_the_first_window_and_cause_no_429()
    {
        await using var harness = new ClientHarness();
        var upstream = new FakeUpstream(harness.Time, limitPerWindow: 5);
        harness.Handler.Script = (request, _, _) => Task.FromResult(upstream.Respond(request));
        var start = harness.Time.GetUtcNow();
        var collector = harness.Services.GetRequiredService<InventoryCollector>();

        var result = await harness.Drive(collector.CollectAsync(Ct));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, upstream.Throttled);
        var times = harness.Handler.Requests.Select(r => r.At - start).Order().ToList();
        Assert.Equal(8, times.Count);
        Assert.All(times.Take(5), t => Assert.Equal(TimeSpan.Zero, t));
        Assert.All(times.Skip(5), t => Assert.True(t >= TimeSpan.FromSeconds(10), $"request at {t} came before the window ended"));
        Assert.True(Pacing.MaxInAnyWindow(harness.Handler.Requests, TimeSpan.FromSeconds(10)) <= 5);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Two reports at once")]
    public async Task Two_collections_share_the_permits_and_never_exceed_five_per_window()
    {
        // Two separate collections, bypassing the report service's sharing, so that 16 requests
        // compete for the permits. The last waits three windows, so the page deadline is raised.
        await using var harness = new ClientHarness(settings: new Dictionary<string, string?>
        {
            ["InventoryApi:PageTimeout"] = "00:01:00",
            ["InventoryApi:CollectionTimeout"] = "00:02:00",
        });
        var upstream = new FakeUpstream(harness.Time, limitPerWindow: 5);
        harness.Handler.Script = (request, _, _) => Task.FromResult(upstream.Respond(request));
        var first = harness.Services.GetRequiredService<InventoryCollector>();
        var second = harness.Services.GetRequiredService<InventoryCollector>();

        var both = Task.WhenAll(first.CollectAsync(Ct), second.CollectAsync(Ct));
        var results = await harness.Drive(both);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(16, harness.Handler.Requests.Count);
        Assert.Equal(0, upstream.Throttled);
        Assert.True(Pacing.MaxInAnyWindow(harness.Handler.Requests, TimeSpan.FromSeconds(10)) <= 5);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Throttled anyway")]
    public async Task A_429_from_a_shared_key_waits_its_retry_after()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var harness = new ClientHarness((request, n, _) => Task.FromResult(
            n == 0 ? ScriptedHttpHandler.Problem(HttpStatusCode.TooManyRequests, retryAfterSeconds: 6) : upstream.Respond(request)));

        var result = await harness.Drive(harness.Client().GetPageAsync(2, 25, Ct));

        Assert.True(result.IsSuccess);
        var requests = harness.Handler.Requests;
        Assert.True(requests[1].At - requests[0].At >= TimeSpan.FromSeconds(6));
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Retry-After honoured")]
    public async Task Retry_after_replaces_the_backoff_delay()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var harness = new ClientHarness((request, n, _) => Task.FromResult(
            n == 0 ? ScriptedHttpHandler.Problem(HttpStatusCode.TooManyRequests, retryAfterSeconds: 2) : upstream.Respond(request)));

        var result = await harness.Drive(harness.Client().GetPageAsync(2, 25, Ct), step: TimeSpan.FromMilliseconds(50));

        Assert.True(result.IsSuccess);
        var requests = harness.Handler.Requests;
        Assert.Equal(2, requests.Count);
        var gap = requests[1].At - requests[0].At;
        Assert.True(gap >= TimeSpan.FromSeconds(2), $"second attempt after {gap}");
        Assert.True(gap < TimeSpan.FromSeconds(2.5), $"second attempt after {gap}");
    }

    [Fact]
    public async Task Retry_after_as_an_http_date_is_honoured()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        ClientHarness? self = null;
        await using var harness = new ClientHarness((request, n, _) =>
        {
            if (n > 0)
            {
                return Task.FromResult(upstream.Respond(request));
            }

            var response = ScriptedHttpHandler.Problem(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(self!.Time.GetUtcNow().AddSeconds(3));
            return Task.FromResult(response);
        });
        self = harness;

        var result = await harness.Drive(harness.Client().GetPageAsync(2, 25, Ct));

        Assert.True(result.IsSuccess);
        Assert.True(harness.Handler.Requests[1].At - harness.Handler.Requests[0].At >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Retry-After too long")]
    public async Task A_retry_after_beyond_the_cap_is_not_waited_for()
    {
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Problem(HttpStatusCode.TooManyRequests, retryAfterSeconds: 120)));

        // Awaited without advancing the fake clock: the client must not wait at all.
        var result = await harness.Client().GetPageAsync(2, 25, Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.UpstreamThrottled, result.Failure.Kind);
        Assert.Equal(TimeSpan.FromSeconds(120), result.Failure.RetryAfter);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Bad credentials")]
    public async Task A_401_fails_fast_as_upstream_misconfigured()
    {
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Problem(HttpStatusCode.Unauthorized)));

        var result = await harness.Client().GetPageAsync(1, 25, Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.UpstreamMisconfigured, result.Failure.Kind);
        Assert.Equal(401, result.Failure.StatusCode);
        Assert.Single(harness.Handler.Requests);
        Assert.Contains("missing or rejected", result.Failure.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientHarness.ApiKey, result.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/A status nobody documented")]
    public async Task A_418_fails_fast_as_upstream_rejected()
    {
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Problem((HttpStatusCode)418)));

        var result = await harness.Client().GetPageAsync(1, 25, Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.UpstreamRejected, result.Failure.Kind);
        Assert.Equal(418, result.Failure.StatusCode);
        Assert.Single(harness.Handler.Requests);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(501)]
    [InlineData(204)]
    public async Task Statuses_no_rule_covers_fail_fast_as_unexpected(int status)
    {
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Problem((HttpStatusCode)status)));

        var result = await harness.Client().GetPageAsync(1, 25, Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.UpstreamUnexpectedStatus, result.Failure.Kind);
        Assert.Equal(status, result.Failure.StatusCode);
        Assert.Single(harness.Handler.Requests);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Upstream hangs")]
    public async Task A_hanging_upstream_times_out_each_attempt_and_the_page()
    {
        await using var harness = new ClientHarness(async (_, _, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ScriptedHttpHandler.Json("{}");
        });
        var start = harness.Time.GetUtcNow();

        var result = await harness.Drive(harness.Client().GetPageAsync(2, 25, Ct));
        var elapsed = harness.Time.GetUtcNow() - start;

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.UpstreamUnavailable, result.Failure.Kind);
        Assert.True(elapsed <= TimeSpan.FromSeconds(30.2), $"failed after {elapsed}; attempts at {string.Join(", ", harness.Handler.Requests.Select(r => r.At - start))}");
        var requests = harness.Handler.Requests;
        Assert.True(requests.Count >= 3, $"{requests.Count} attempts");
        for (var i = 1; i < requests.Count; i++)
        {
            Assert.True(requests[i].At - requests[i - 1].At >= TimeSpan.FromSeconds(10), "an attempt ended before the attempt timeout");
        }
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Truncated body")]
    public async Task A_truncated_200_body_is_malformed_and_not_retried()
    {
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Json("""{"records":[{"id":1001,"name":"Item""")));

        var result = await harness.Client().GetPageAsync(1, 25, Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.MalformedResponse, result.Failure.Kind);
        Assert.Single(harness.Handler.Requests);
    }

    [Theory]
    [InlineData("""{"page":1,"pageSize":25,"totalPages":7,"totalRecords":157}""")]
    [InlineData("""{"records":[],"pageSize":25,"totalPages":7,"totalRecords":157}""")]
    [InlineData("""{"records":[],"page":"1","pageSize":25,"totalPages":7,"totalRecords":157}""")]
    [InlineData("""[]""")]
    public async Task An_incomplete_envelope_is_malformed(string body)
    {
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Json(body)));

        var result = await harness.Client().GetPageAsync(1, 25, Ct);

        Assert.Equal(FailureKinds.MalformedResponse, result.Failure?.Kind);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/One record missing its price")]
    public async Task A_record_without_a_price_does_not_fail_the_page()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        var body = upstream.PageJson(1, 25, records =>
        {
            var copy = records.Select(r => new Dictionary<string, object?>(r)).ToList();
            copy[4].Remove("price");
            copy[5]["unexpected"] = "ignored";
            return copy;
        });
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Json(body)));

        var result = await harness.Client().GetPageAsync(1, 25, Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(25, result.Page.Records.Count);
        Assert.Null(result.Page.Records[4].Price);
        Assert.Empty(result.Page.Records[4].MalformedFields);
        Assert.Equal(24, result.Page.Records.Count(r => r.Price is not null));
    }

    [Fact]
    public async Task Prices_are_read_as_exact_decimals()
    {
        const string body = """{"records":[{"id":1001,"name":"A","category":"Toys","price":0.1,"quantity":3,"lastUpdated":"2026-10-15T12:30:00Z"}],"page":1,"pageSize":25,"totalPages":1,"totalRecords":1}""";
        await using var harness = new ClientHarness((_, _, _) => Task.FromResult(ScriptedHttpHandler.Json(body)));

        var result = await harness.Client().GetPageAsync(1, 25, Ct);

        Assert.Equal(0.1m, Assert.Single(result.Page!.Records).Price);
    }

    [Fact]
    [Trait("Scenario", "inventory-source/Log content")]
    public async Task Each_retry_is_logged_as_a_warning_and_the_key_never_is()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var harness = new ClientHarness((request, n, _) => Task.FromResult(
            n < 2 ? ScriptedHttpHandler.Problem(HttpStatusCode.InternalServerError) : upstream.Respond(request)));

        var result = await harness.Drive(harness.Client().GetPageAsync(2, 25, Ct));

        Assert.True(result.IsSuccess);
        var retries = harness.Logs.GetSnapshot().Where(r => r.Level == LogLevel.Warning && r.Message.Contains("retrying", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, retries.Count);
        Assert.StartsWith("Page 2 attempt 1 failed with status 500; retrying in ", retries[0].Message, StringComparison.Ordinal);
        Assert.StartsWith("Page 2 attempt 2 failed with status 500; retrying in ", retries[1].Message, StringComparison.Ordinal);
        Assert.All(harness.Logs.GetSnapshot(), r => Assert.DoesNotContain(ClientHarness.ApiKey, r.Message + r.Exception, StringComparison.Ordinal));
    }
}
