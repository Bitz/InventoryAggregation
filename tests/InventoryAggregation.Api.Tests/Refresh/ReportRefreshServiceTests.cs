using System.Net;
using System.Text.Json;
using InventoryAggregation.Api.Tests.Support;
using InventoryAggregation.Core.Reporting;
using Microsoft.Extensions.Logging;

namespace InventoryAggregation.Api.Tests.Refresh;

/// <summary>The background refresh as the host runs it: started with the host, stopped with it.</summary>
public sealed class ReportRefreshServiceTests
{
    private const string Route = "/api/inventory/categories";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "report-refresh/Startup refresh")]
    public async Task The_first_refresh_starts_with_the_host_without_holding_it_up()
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
        using (var during = await client.GetAsync(Route, Ct))
        {
            // The host is up and answering while the refresh runs.
            Assert.Equal(HttpStatusCode.ServiceUnavailable, during.StatusCode);
        }

        release.SetResult();
        await FakeClock.Until(() => api.Store.Snapshot.Latest is not null, "the startup refresh to store its report");
        using var after = await client.GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        var freshness = JsonDocument.Parse(await after.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("freshness");
        Assert.Equal("startup", freshness.GetProperty("trigger").GetString());
        Assert.Equal(8, source.Calls.Count);
    }

    [Fact]
    [Trait("Scenario", "report-refresh/Shutdown")]
    public async Task Stopping_the_host_cancels_the_collection_and_records_no_failure()
    {
        var hold = false;
        var cancelled = 0;
        var source = new FakePageSource(async (page, _, token) =>
        {
            if (page > 1 && Volatile.Read(ref hold))
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
        var api = new TestApi { PageSource = source };
        await api.StartedAsync();
        var store = api.Store;
        var logs = api.Logs;
        var before = store.Snapshot;
        Volatile.Write(ref hold, true);

        await api.ParkThenAdvanceAsync(TimeSpan.FromMinutes(5));
        await FakeClock.Until(() => source.Calls.Count == 8 + 5, "the scheduled collection to have four pages in flight");
        await api.DisposeAsync();

        Assert.Equal(4, Volatile.Read(ref cancelled));
        Assert.Same(before, store.Snapshot);
        Assert.Equal(RefreshTrigger.Startup, store.Snapshot.LastAttempt!.Trigger);
        Assert.True(store.Snapshot.LastAttempt.Succeeded);
        Assert.DoesNotContain(logs.GetSnapshot(), r => r.Level >= LogLevel.Warning);
        Assert.Contains(logs.GetSnapshot(), r => r.Message == "The scheduled refresh was cancelled because the service is stopping");
    }
}
