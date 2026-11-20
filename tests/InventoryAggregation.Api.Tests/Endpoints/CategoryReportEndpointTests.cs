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
    public async Task A_stored_report_gives_200_with_a_summary_that_adds_up()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        await using var api = new TestApi { Upstream = (request, _, _) => Task.FromResult(upstream.Respond(request)) };
        var client = await api.StartedAsync();

        using var response = await client.GetAsync(Route, Ct);

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
        Assert.Equal(["categories", "summary", "freshness"], root.EnumerateObject().Select(p => p.Name));

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
    [Trait("Scenario", "category-report-endpoint/No upstream request")]
    public async Task A_plain_request_is_answered_from_the_store()
    {
        var source = new FakePageSource(page => FakePageSource.Healthy(page));
        await using var api = new TestApi { PageSource = source };
        var client = await api.StartedAsync();
        var calls = source.Calls.Count;

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync(Route, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(8, calls);
        Assert.Equal(calls, source.Calls.Count);
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
        var client = await api.StartedAsync();

        var text = await client.GetStringAsync(Route, Ct);

        Assert.Contains("\"price\":0.005", text, StringComparison.Ordinal);
        Assert.Contains("\"price\":149.99", text, StringComparison.Ordinal);
        Assert.Contains("\"totalInventoryValue\":1799.89,", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Fresh report")]
    public async Task The_age_is_in_the_body_and_the_age_header()
    {
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };
        var client = await api.StartedAsync();
        await api.ParkThenAdvanceAsync(TimeSpan.FromSeconds(42));

        using var response = await client.GetAsync(Route, Ct);
        var root = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(42), response.Headers.Age);
        var freshness = root.GetProperty("freshness");
        Assert.Equal(42, freshness.GetProperty("ageSeconds").GetInt64());
        Assert.Equal(root.GetProperty("summary").GetProperty("generatedAt").GetString(), freshness.GetProperty("asOf").GetString());
        Assert.Equal("2026-11-20T15:04:05Z", freshness.GetProperty("asOf").GetString());
        Assert.Equal("startup", freshness.GetProperty("trigger").GetString());
        Assert.Equal("2026-11-20T15:09:05Z", freshness.GetProperty("nextRefreshAt").GetString());
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Healthy refresh")]
    public async Task After_a_good_refresh_the_report_is_neither_stale_nor_overdue()
    {
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };
        var client = await api.StartedAsync();
        await api.NextScheduledRefreshAsync();

        var freshness = (await ReadJsonAsync(await client.GetAsync(Route, Ct))).GetProperty("freshness");

        Assert.False(freshness.GetProperty("stale").GetBoolean());
        Assert.False(freshness.GetProperty("overdue").GetBoolean());
        Assert.Equal("scheduled", freshness.GetProperty("trigger").GetString());
        var last = freshness.GetProperty("lastRefresh");
        Assert.Equal(["startedAt", "outcome"], last.EnumerateObject().Select(p => p.Name));
        Assert.Equal("success", last.GetProperty("outcome").GetString());
        Assert.Equal("2026-11-20T15:09:05Z", last.GetProperty("startedAt").GetString());
        Assert.Equal("2026-11-20T15:14:05Z", freshness.GetProperty("nextRefreshAt").GetString());
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Overdue report")]
    public async Task A_report_older_than_stale_after_is_served_as_overdue()
    {
        var failing = false;
        var source = new FakePageSource(page => Volatile.Read(ref failing)
            ? FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)
            : FakePageSource.Healthy(page));
        await using var api = new TestApi { PageSource = source };
        var client = await api.StartedAsync();
        Volatile.Write(ref failing, true);

        await api.ParkThenAdvanceAsync(TimeSpan.FromMinutes(61));
        await FakeClock.Until(() => api.Store.Snapshot.IsStale && !api.Refresher.Status().IsRefreshing, "a failed refresh after an hour");
        using var response = await client.GetAsync(Route, Ct);
        var freshness = (await ReadJsonAsync(response)).GetProperty("freshness");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(freshness.GetProperty("overdue").GetBoolean());
        Assert.True(freshness.GetProperty("stale").GetBoolean());
        Assert.Equal(61 * 60, freshness.GetProperty("ageSeconds").GetInt64());
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Start in whole seconds")]
    public async Task The_last_refresh_start_is_truncated_to_whole_seconds()
    {
        var failing = true;
        var source = new FakePageSource(page => Volatile.Read(ref failing)
            ? FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)
            : FakePageSource.Healthy(page));
        await using var api = new TestApi { PageSource = source };
        var client = await api.StartedAsync();
        await api.ParkThenAdvanceAsync(TimeSpan.FromMilliseconds(1700));

        using var notReady = await client.GetAsync($"{Route}?fresh=true", Ct);
        var problem = await ReadProblemAsync(notReady);
        Volatile.Write(ref failing, false);
        using var ok = await client.GetAsync($"{Route}?fresh=true", Ct);
        var freshness = (await ReadJsonAsync(ok)).GetProperty("freshness");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        Assert.Equal("2026-11-20T15:04:06Z", problem.GetProperty("lastRefresh").GetProperty("startedAt").GetString());
        Assert.Equal("2026-11-20T15:04:06Z", freshness.GetProperty("lastRefresh").GetProperty("startedAt").GetString());
        Assert.Equal("2026-11-20T15:04:06.7Z", freshness.GetProperty("asOf").GetString());
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 15, 4, 6, 700, TimeSpan.Zero), api.Store.Snapshot.LastAttempt!.StartedAt);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Upstream down after a good report")]
    public async Task A_failed_refresh_after_a_good_one_serves_the_old_figures_as_stale()
    {
        var failing = false;
        var source = new FakePageSource(page => Volatile.Read(ref failing)
            ? FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)
            : FakePageSource.Healthy(page));
        await using var api = new TestApi { PageSource = source };
        var client = await api.StartedAsync();
        var before = await ReadJsonAsync(await client.GetAsync(Route, Ct));
        Volatile.Write(ref failing, true);

        await api.NextScheduledRefreshAsync();
        using var response = await client.GetAsync(Route, Ct);
        var after = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(before.GetProperty("categories").GetRawText(), after.GetProperty("categories").GetRawText());
        Assert.Equal(before.GetProperty("summary").GetRawText(), after.GetProperty("summary").GetRawText());
        var freshness = after.GetProperty("freshness");
        Assert.True(freshness.GetProperty("stale").GetBoolean());
        Assert.Equal("startup", freshness.GetProperty("trigger").GetString());
        var last = freshness.GetProperty("lastRefresh");
        Assert.Equal(FailureKinds.UpstreamUnavailable, last.GetProperty("outcome").GetString());
        Assert.Equal("2026-11-20T15:09:05Z", last.GetProperty("startedAt").GetString());
        Assert.False(string.IsNullOrEmpty(last.GetProperty("detail").GetString()));
        Assert.Equal(300, freshness.GetProperty("ageSeconds").GetInt64());
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Wrong key after a good report")]
    public async Task A_rejected_key_after_a_good_report_is_stale_and_the_detail_has_no_key()
    {
        var upstream = new FakeUpstream(TimeProvider.System);
        var rejecting = false;
        await using var api = new TestApi
        {
            Upstream = (request, _, _) => Task.FromResult(Volatile.Read(ref rejecting)
                ? ScriptedHttpHandler.Problem(HttpStatusCode.Unauthorized)
                : upstream.Respond(request)),
        };
        var client = await api.StartedAsync();
        Volatile.Write(ref rejecting, true);

        await api.NextScheduledRefreshAsync();
        using var response = await client.GetAsync(Route, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var freshness = JsonDocument.Parse(text).RootElement.GetProperty("freshness");
        Assert.True(freshness.GetProperty("stale").GetBoolean());
        Assert.Equal(FailureKinds.UpstreamMisconfigured, freshness.GetProperty("lastRefresh").GetProperty("outcome").GetString());
        Assert.Contains("401", freshness.GetProperty("lastRefresh").GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ClientHarness.ApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("inventory.test", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Wrong key at startup")]
    public async Task A_rejected_key_from_the_start_gives_503_report_not_ready_without_the_key()
    {
        await using var api = new TestApi { Upstream = (_, _, _) => Task.FromResult(ScriptedHttpHandler.Problem(HttpStatusCode.Unauthorized)) };
        var client = await api.StartedAsync();

        using var response = await client.GetAsync(Route, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonDocument.Parse(text).RootElement;
        Assert.Equal(FailureKinds.ReportNotReady, problem.GetProperty("kind").GetString());
        Assert.Equal("urn:inventory-aggregation:report-not-ready", problem.GetProperty("type").GetString());
        Assert.Equal(503, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("title").GetString()));
        Assert.Contains(FailureKinds.UpstreamMisconfigured, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        var last = problem.GetProperty("lastRefresh");
        Assert.Equal(FailureKinds.UpstreamMisconfigured, last.GetProperty("outcome").GetString());
        Assert.Equal("2026-11-20T15:04:05Z", last.GetProperty("startedAt").GetString());
        Assert.False(problem.TryGetProperty("categories", out _));
        Assert.DoesNotContain(ClientHarness.ApiKey, text, StringComparison.Ordinal);
        Assert.DoesNotContain("inventory.test", text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Forced report with nothing stored")]
    public async Task A_failed_forced_collection_with_nothing_stored_gives_503()
    {
        var source = new FakePageSource(page => FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page));
        await using var api = new TestApi { PageSource = source };
        var client = await api.StartedAsync();
        var calls = source.Calls.Count;
        await api.ParkThenAdvanceAsync(TimeSpan.FromSeconds(5));

        using var response = await client.GetAsync($"{Route}?fresh=true", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(FailureKinds.ReportNotReady, problem.GetProperty("kind").GetString());
        var last = problem.GetProperty("lastRefresh");
        Assert.Equal(FailureKinds.UpstreamUnavailable, last.GetProperty("outcome").GetString());
        Assert.Equal("2026-11-20T15:04:10Z", last.GetProperty("startedAt").GetString());
        Assert.True(source.Calls.Count > calls, "the forced request ran a collection");
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Request during the startup refresh")]
    public async Task A_request_while_the_first_refresh_runs_gets_503_with_retry_after_the_window()
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
        await FakeClock.Until(() => source.Calls.Contains(2), "the startup refresh to be collecting");

        using var response = await client.GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(10), response.Headers.RetryAfter?.Delta);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(FailureKinds.ReportNotReady, problem.GetProperty("kind").GetString());
        Assert.False(problem.TryGetProperty("lastRefresh", out _));
        release.SetResult();
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Between failed attempts")]
    public async Task Between_failed_attempts_retry_after_is_the_time_to_the_next_plus_the_window()
    {
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)) };
        var client = await api.StartedAsync();
        await api.ParkThenAdvanceAsync(TimeSpan.FromSeconds(5));

        using var response = await client.GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(35), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Caller disconnects during a forced collection")]
    public async Task A_forced_caller_that_leaves_stops_waiting_and_the_collection_carries_on()
    {
        var hold = false;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = 0;
        var source = new FakePageSource(async (page, _, token) =>
        {
            if (page == 2 && Volatile.Read(ref hold))
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
        var client = await api.StartedAsync();
        Volatile.Write(ref hold, true);
        await api.ParkThenAdvanceAsync(TimeSpan.FromMinutes(2));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var request = client.GetAsync($"{Route}?fresh=true", caller.Token);
        await FakeClock.Until(() => source.Calls.Count(p => p == 2) == 2, "the forced collection to be fetching pages");
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        release.SetResult();
        await FakeClock.Until(() => api.Store.Snapshot.Latest?.Trigger == Core.Reporting.RefreshTrigger.Fresh, "the forced collection to store its report");

        Assert.Equal(0, Volatile.Read(ref cancelled));
        Assert.Equal(16, source.Calls.Count);
        Assert.DoesNotContain(api.Logs.GetSnapshot(), r => r.Level >= LogLevel.Error);
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/A bug in the service")]
    public async Task An_unhandled_exception_gives_500_problem_details_without_a_stack_trace()
    {
        await using var api = new TestApi { PageSource = new FakePageSource(_ => throw new InvalidOperationException("internal detail that must not leak")) };
        var client = await api.StartedAsync();

        using var response = await client.GetAsync($"{Route}?fresh=true", Ct);
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
        var client = await api.StartedAsync();

        using var response = await client.GetAsync($"{Route}?fresh=true", Ct);

        Assert.Contains("visible in development", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_fresh_value_gives_400()
    {
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };
        var client = await api.StartedAsync();

        using var response = await client.GetAsync($"{Route}?fresh=maybe", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Every kind a collection can end with (design.md §5); <c>report-not-ready</c> is the endpoint's own.</summary>
    public static TheoryData<string> CollectionKinds() =>
        [.. typeof(FailureKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(k => k != FailureKinds.ReportNotReady)];

    [Theory]
    [MemberData(nameof(CollectionKinds))]
    [Trait("Scenario", "category-report-endpoint/Endpoint tests with a fake service")]
    public async Task Each_failure_kind_after_a_good_report_is_200_stale(string kind)
    {
        var failing = false;
        var source = new FakePageSource(page => Volatile.Read(ref failing)
            ? FakePageSource.Fail(kind, page, retryAfter: TimeSpan.FromSeconds(3))
            : FakePageSource.Healthy(page));
        await using var api = new TestApi { PageSource = source };
        var client = await api.StartedAsync();
        Volatile.Write(ref failing, true);
        await api.NextScheduledRefreshAsync();

        using var response = await client.GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await ReadJsonAsync(response);
        Assert.NotEmpty(root.GetProperty("categories").EnumerateArray());
        var freshness = root.GetProperty("freshness");
        Assert.True(freshness.GetProperty("stale").GetBoolean());
        Assert.Equal(kind, freshness.GetProperty("lastRefresh").GetProperty("outcome").GetString());
    }

    [Theory]
    [MemberData(nameof(CollectionKinds))]
    [Trait("Scenario", "category-report-endpoint/Endpoint tests with a fake service")]
    public async Task Each_failure_kind_with_no_good_report_is_503_report_not_ready(string kind)
    {
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Fail(kind, page, retryAfter: TimeSpan.FromSeconds(3))) };
        var client = await api.StartedAsync();

        using var response = await client.GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(FailureKinds.ReportNotReady, problem.GetProperty("kind").GetString());
        Assert.Equal(kind, problem.GetProperty("lastRefresh").GetProperty("outcome").GetString());
        Assert.False(problem.TryGetProperty("categories", out _));
    }

    [Fact]
    public void Only_the_two_response_kinds_have_a_status()
    {
        Assert.Equal([FailureKinds.InternalError, FailureKinds.ReportNotReady], FailureResponses.Kinds.Order());
        Assert.Equal(503, FailureResponses.StatusFor(FailureKinds.ReportNotReady));
        Assert.Equal(500, FailureResponses.StatusFor(FailureKinds.InternalError));
    }

    [Fact]
    [Trait("Scenario", "category-report-endpoint/Development")]
    public async Task The_openapi_document_and_swagger_ui_are_served_in_development()
    {
        await using var api = new TestApi { Environment = "Development", PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };
        var client = api.CreateClient();

        using var ui = await client.GetAsync("/swagger/index.html", Ct);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal("text/html", ui.Content.Headers.ContentType?.MediaType);
        var uiConfig = await client.GetStringAsync("/swagger/index.js", Ct) + await ui.Content.ReadAsStringAsync(Ct);
        Assert.Contains("/openapi/v1.json", uiConfig, StringComparison.Ordinal);

        using var response = await client.GetAsync("/openapi/v1.json", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var operation = document.RootElement.GetProperty("paths").GetProperty(Route).GetProperty("get");
        Assert.Equal(["200", "400", "500", "503"], operation.GetProperty("responses").EnumerateObject().Select(p => p.Name).Order());
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToList();
        Assert.Equal(["fresh", "include"], parameters.Select(p => p.GetProperty("name").GetString()).Order());
        Assert.All(parameters, p => Assert.Equal("query", p.GetProperty("in").GetString()));
    }

    [Theory]
    [Trait("Scenario", "category-report-endpoint/Production")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger")]
    public async Task Neither_the_document_nor_swagger_ui_is_served_in_production(string path)
    {
        await using var api = new TestApi { PageSource = new FakePageSource(page => FakePageSource.Healthy(page)) };

        using var response = await api.CreateClient().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
        }
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }
}
