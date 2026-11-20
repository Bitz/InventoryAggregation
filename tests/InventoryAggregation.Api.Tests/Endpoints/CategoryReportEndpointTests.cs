using System.Net;
using System.Reflection;
using System.Text.Json;
using InventoryAggregation.Api.Endpoints;
using InventoryAggregation.Api.Tests.Support;
using InventoryAggregation.Core;
using InventoryAggregation.Core.Collection;
using Microsoft.Extensions.Logging;

namespace InventoryAggregation.Api.Tests.Endpoints;

public sealed class CategoryReportEndpointTests
{
    private const string Route = "/api/inventory/categories";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Healthy upstream")]
    public async Task A_healthy_upstream_gives_200_with_a_summary_that_adds_up()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var api = new TestApi { Upstream = (request, _, _) => Task.FromResult(upstream.Respond(request)) };

        using var response = await api.CreateClient().GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = body.RootElement;
        var categories = root.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(["Electronics", "Office Supplies", "Toys"], categories.Select(c => c.GetProperty("category").GetString()));

        var summary = root.GetProperty("summary");
        var total = summary.GetProperty("totalRecords").GetInt32();
        var available = summary.GetProperty("availableRecords").GetInt32();
        var zero = summary.GetProperty("zeroQuantityExcluded").GetInt32();
        var rejected = summary.GetProperty("rejectedRecords").GetArrayLength();
        Assert.Equal(157, total);
        Assert.Equal(total, available + zero + rejected);
        Assert.Equal(available, categories.Sum(c => c.GetProperty("availableRecordCount").GetInt32()));
        Assert.Equal(7, summary.GetProperty("pagesFetched").GetInt32());
        Assert.Equal(8, summary.GetProperty("requests").GetInt32());
        Assert.EndsWith("Z", summary.GetProperty("generatedAt").GetString(), StringComparison.Ordinal);

        foreach (var category in categories)
        {
            var items = category.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(category.GetProperty("availableRecordCount").GetInt32(), items.Count);
            Assert.Equal(
                category.GetProperty("totalInventoryValue").GetDecimal(),
                Math.Round(items.Sum(i => i.GetProperty("price").GetDecimal() * i.GetProperty("quantity").GetDecimal()), 2, MidpointRounding.AwayFromZero));
            Assert.All(items, i => Assert.EndsWith("Z", i.GetProperty("lastUpdated").GetString(), StringComparison.Ordinal));
            var mostRecent = category.GetProperty("mostRecentlyUpdated");
            Assert.Equal(["id", "name", "lastUpdated"], mostRecent.EnumerateObject().Select(p => p.Name));
        }
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Price with more than 2 places")]
    public async Task Item_prices_are_echoed_and_the_value_rounded_once()
    {
        const string body = """
            {"records":[
              {"id":1,"name":"Fine","category":"Parts","price":0.005,"quantity":1,"lastUpdated":"2026-10-15T12:30:00Z"},
              {"id":2,"name":"Printer","category":"Parts","price":149.99,"quantity":12,"lastUpdated":"2026-10-15T12:31:00Z"}],
             "page":1,"pageSize":25,"totalPages":1,"totalRecords":2}
            """;
        await using var api = new TestApi
        {
            Upstream = (request, _, _) => Task.FromResult(ScriptedHttpHandler.Json(request.RequestUri!.Query.Contains("page=1&", StringComparison.Ordinal)
                ? body
                : """{"records":[],"page":2,"pageSize":25,"totalPages":1,"totalRecords":2}""")),
        };

        var text = await api.CreateClient().GetStringAsync(Route, Ct);

        Assert.Contains("\"price\":0.005", text, StringComparison.Ordinal);
        Assert.Contains("\"price\":149.99", text, StringComparison.Ordinal);
        Assert.Contains("\"totalInventoryValue\":1799.89,", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Upstream down")]
    public async Task An_unreachable_upstream_gives_502_upstream_unavailable()
    {
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)) };

        var problem = await GetProblemAsync(api, HttpStatusCode.BadGateway);

        Assert.Equal(FailureKinds.UpstreamUnavailable, problem.GetProperty("kind").GetString());
        Assert.Equal("urn:inventory-aggregation:upstream-unavailable", problem.GetProperty("type").GetString());
        Assert.False(problem.TryGetProperty("categories", out _));
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Upstream throttling")]
    public async Task Upstream_throttling_gives_503_with_retry_after()
    {
        await using var api = new TestApi
        {
            Upstream = (_, _, _) => Task.FromResult(ScriptedHttpHandler.Problem(HttpStatusCode.TooManyRequests, retryAfterSeconds: 120)),
        };

        using var response = await api.CreateClient().GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(120), response.Headers.RetryAfter?.Delta);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(FailureKinds.UpstreamThrottled, problem.GetProperty("kind").GetString());
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Wrong key")]
    public async Task A_rejected_key_gives_500_misconfigured_without_the_key()
    {
        await using var api = new TestApi { Upstream = (_, _, _) => Task.FromResult(ScriptedHttpHandler.Problem(HttpStatusCode.Unauthorized)) };

        using var response = await api.CreateClient().GetAsync(Route, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(FailureKinds.UpstreamMisconfigured, JsonDocument.Parse(text).RootElement.GetProperty("kind").GetString());
        Assert.DoesNotContain(ClientHarness.ApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("inventory.test", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Data does not reconcile")]
    public async Task An_incomplete_dataset_gives_502_with_both_counts_and_no_categories()
    {
        // Page 3 repeats page 2's last record in place of its own last one, on every pass.
        var source = new FakePageSource(page =>
        {
            var result = FakePageSource.Healthy(page);
            if (page != 3)
            {
                return result;
            }

            var previousLast = FakePageSource.Healthy(2).Page!.Records[^1];
            return PageResult.Success(result.Page! with { Records = [previousLast, .. result.Page!.Records.Take(24)] }, 1);
        });
        await using var api = new TestApi { PageSource = source };

        var problem = await GetProblemAsync(api, HttpStatusCode.BadGateway);

        Assert.Equal(FailureKinds.IncompleteDataset, problem.GetProperty("kind").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains("157", detail, StringComparison.Ordinal);
        Assert.Contains("156", detail, StringComparison.Ordinal);
        Assert.Equal(157, problem.GetProperty("expectedCount").GetInt32());
        Assert.Equal(156, problem.GetProperty("collectedCount").GetInt32());
        Assert.False(problem.TryGetProperty("categories", out _));
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Deadline")]
    public async Task Exceeding_the_collection_timeout_gives_504()
    {
        var source = new FakePageSource(async (page, _, token) =>
        {
            if (page == 2)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return FakePageSource.Healthy(page);
        });
        await using var api = new TestApi { PageSource = source };
        var client = api.CreateClient();

        var request = client.GetAsync(Route, Ct);
        await FakeClock.Until(() => source.Calls.Contains(2), "page 2 to be requested");
        var response = await FakeClock.Drive(api.Time, request, TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(FailureKinds.UpstreamTimeout, (await ReadProblemAsync(response)).GetProperty("kind").GetString());
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/A bug in the service")]
    public async Task An_unhandled_exception_gives_500_problem_details_without_a_stack_trace()
    {
        await using var api = new TestApi { PageSource = new FakePageSource(_ => throw new InvalidOperationException("internal detail that must not leak")) };

        using var response = await api.CreateClient().GetAsync(Route, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(text).RootElement;
        Assert.Equal(FailureKinds.InternalError, problem.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("internal detail", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
        Assert.Contains(api.Logs.GetSnapshot(), r => r.Level == LogLevel.Error && r.Exception is InvalidOperationException);
    }

    [Fact]
    public async Task In_development_the_exception_is_shown()
    {
        await using var api = new TestApi
        {
            Environment = "Development",
            PageSource = new FakePageSource(_ => throw new InvalidOperationException("visible in development")),
        };

        using var response = await api.CreateClient().GetAsync(Route, Ct);

        Assert.Contains("visible in development", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Caller disconnects mid-collection")]
    public async Task A_caller_that_leaves_cancels_upstream_work_without_logging_an_error()
    {
        var cancelled = 0;
        var source = new FakePageSource(async (page, _, token) =>
        {
            if (page > 1)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    throw;
                }
            }

            return FakePageSource.Healthy(page);
        });
        await using var api = new TestApi { PageSource = source };
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var request = api.CreateClient().GetAsync(Route, caller.Token);
        await FakeClock.Until(() => source.Calls.Count == 5, "four pages to be in flight");
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await FakeClock.Until(() => Volatile.Read(ref cancelled) == 4, "every in-flight page to be cancelled");
        Assert.DoesNotContain(api.Logs.GetSnapshot(), r => r.Level >= LogLevel.Error);
    }

    public static TheoryData<string> AllKinds() =>
        [.. typeof(FailureKinds).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetRawConstantValue()!)];

    [Theory]
    [MemberData(nameof(AllKinds))]
    [Trait("Scenario", "category-report-endpoint/Endpoint tests with a fake service")]
    public async Task Each_failure_kind_maps_to_its_status(string kind)
    {
        var expected = kind switch
        {
            FailureKinds.UpstreamThrottled => HttpStatusCode.ServiceUnavailable,
            FailureKinds.UpstreamTimeout => HttpStatusCode.GatewayTimeout,
            FailureKinds.UpstreamMisconfigured or FailureKinds.InternalError => HttpStatusCode.InternalServerError,
            _ => HttpStatusCode.BadGateway,
        };
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Fail(kind, page, retryAfter: TimeSpan.FromSeconds(3))) };

        using var response = await api.CreateClient().GetAsync(Route, Ct);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal((int)expected, FailureResponses.StatusFor(kind));
        var problem = await ReadProblemAsync(response);
        Assert.Equal(kind, problem.GetProperty("kind").GetString());
        Assert.Equal($"urn:inventory-aggregation:{kind}", problem.GetProperty("type").GetString());
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
    }

    [Fact]
    public void Every_failure_kind_has_a_mapping()
    {
        Assert.Equal(AllKinds().Select(k => k.Data).Order(), FailureResponses.Kinds.Order());
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Development")]
    public async Task The_openapi_document_is_served_in_development()
    {
        await using var api = new TestApi { Environment = "Development", PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };

        using var response = await api.CreateClient().GetAsync("/openapi/v1.json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var responses = document.RootElement.GetProperty("paths").GetProperty(Route).GetProperty("get").GetProperty("responses");
        Assert.Equal(["200", "500", "502", "503", "504"], responses.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Production")]
    public async Task The_openapi_document_is_not_served_in_production()
    {
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };

        using var response = await api.CreateClient().GetAsync("/openapi/v1.json", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Two callers at once")]
    public async Task Two_callers_at_once_share_one_collection_and_get_the_same_report()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakePageSource(async (page, _, token) =>
        {
            if (page == 2)
            {
                await release.Task.WaitAsync(token);
            }

            return FakePageSource.Healthy(page);
        });
        await using var api = new TestApi { PageSource = source };
        var client = api.CreateClient();

        var first = client.GetAsync(Route, Ct);
        await FakeClock.Until(() => source.Calls.Contains(2), "the first collection to be running");
        var second = client.GetAsync(Route, Ct);
        await FakeClock.Until(() => api.Logs.GetSnapshot().Any(r => r.Message.Contains("joining it", StringComparison.Ordinal)), "the second caller to join");
        release.SetResult();

        using var a = await first;
        using var b = await second;
        Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        Assert.Equal(await a.Content.ReadAsStringAsync(Ct), await b.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, source.Calls.Count(p => p == 1));
        Assert.Equal(8, source.Calls.Count);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Shared failure")]
    public async Task Two_callers_sharing_a_failed_collection_get_the_same_502()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakePageSource(async (page, _, token) =>
        {
            if (page == 2)
            {
                await release.Task.WaitAsync(token);
                return PageResult.Success(FakePageSource.Healthy(2).Page! with { TotalRecords = 160 }, 1);
            }

            return FakePageSource.Healthy(page);
        });
        await using var api = new TestApi { PageSource = source };
        var client = api.CreateClient();

        var first = client.GetAsync(Route, Ct);
        await FakeClock.Until(() => source.Calls.Contains(2), "the first collection to be running");
        var second = client.GetAsync(Route, Ct);
        await FakeClock.Until(() => api.Logs.GetSnapshot().Any(r => r.Message.Contains("joining it", StringComparison.Ordinal)), "the second caller to join");
        release.SetResult();

        using var a = await first;
        using var b = await second;
        Assert.Equal(HttpStatusCode.BadGateway, a.StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, b.StatusCode);
        var pa = await ReadProblemAsync(a);
        var pb = await ReadProblemAsync(b);
        Assert.Equal(FailureKinds.InconsistentPaging, pa.GetProperty("kind").GetString());
        foreach (var field in new[] { "type", "title", "status", "detail", "kind", "page" })
        {
            Assert.Equal(pa.GetProperty(field).ToString(), pb.GetProperty(field).ToString());
        }
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/One caller leaves")]
    public async Task When_one_of_two_callers_leaves_the_collection_continues_for_the_other()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = 0;
        var source = new FakePageSource(async (page, _, token) =>
        {
            if (page == 2)
            {
                try
                {
                    await release.Task.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    throw;
                }
            }

            return FakePageSource.Healthy(page);
        });
        await using var api = new TestApi { PageSource = source };
        var client = api.CreateClient();
        using var leaver = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var first = client.GetAsync(Route, leaver.Token);
        await FakeClock.Until(() => source.Calls.Contains(2), "the first collection to be running");
        var second = client.GetAsync(Route, Ct);
        await FakeClock.Until(() => api.Logs.GetSnapshot().Any(r => r.Message.Contains("joining it", StringComparison.Ordinal)), "the second caller to join");
        await leaver.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();

        using var b = await second;
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        Assert.Equal(0, cancelled);
        Assert.Equal(1, source.Calls.Count(p => p == 1));
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Caller after completion")]
    public async Task A_caller_after_completion_starts_a_new_collection()
    {
        var source = new FakePageSource(page => FakePageSource.Healthy(page));
        await using var api = new TestApi { PageSource = source };
        var client = api.CreateClient();

        var first = await client.GetStringAsync(Route, Ct);
        api.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await client.GetStringAsync(Route, Ct);

        Assert.Equal(2, source.Calls.Count(p => p == 1));
        Assert.NotEqual(
            JsonDocument.Parse(first).RootElement.GetProperty("summary").GetProperty("generatedAt").GetString(),
            JsonDocument.Parse(second).RootElement.GetProperty("summary").GetProperty("generatedAt").GetString());
    }

    private static async Task<JsonElement> GetProblemAsync(TestApi api, HttpStatusCode expected)
    {
        using var response = await api.CreateClient().GetAsync(Route, Ct);
        Assert.Equal(expected, response.StatusCode);
        return await ReadProblemAsync(response);
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }
}
