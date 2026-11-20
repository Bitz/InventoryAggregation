using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using InventoryAggregation.Api.Tests.Support;
using InventoryAggregation.Core;
using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace InventoryAggregation.Api.Tests.Lineage;

public sealed class DataLineageTests
{
    private const string Route = "/api/inventory/categories";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "data-lineage/Audit requested")]
    public async Task Include_audit_adds_the_run_lineage()
    {
        await using var api = WithFakeUpstream(out _);

        var audit = await GetAuditAsync(api);

        Assert.Equal(["runId", "startedAt", "finishedAt", "fingerprint", "extract", "transform", "load"], audit.EnumerateObject().Select(p => p.Name));
        Assert.Matches("^[0-9a-f]{6}$", audit.GetProperty("runId").GetString());
        Assert.EndsWith("Z", audit.GetProperty("startedAt").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("Z", audit.GetProperty("finishedAt").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("sha256:", audit.GetProperty("fingerprint").GetString(), StringComparison.Ordinal);
        Assert.Equal(157, audit.GetProperty("extract").GetProperty("reportedTotalRecords").GetInt32());
    }

    [Fact]
    [Trait("Scenario", "data-lineage/Audit not requested")]
    public async Task Without_include_there_is_no_audit()
    {
        await using var api = WithFakeUpstream(out _);

        var body = await api.CreateClient().GetStringAsync(Route, Ct);

        Assert.False(JsonDocument.Parse(body).RootElement.TryGetProperty("audit", out _));
    }

    [Fact]
    [Trait("Scenario", "data-lineage/Overlapping pages")]
    public async Task Each_page_overlap_is_listed_with_both_pages()
    {
        await using var api = WithFakeUpstream(out _);

        var extract = (await GetAuditAsync(api)).GetProperty("extract");

        var collapsed = extract.GetProperty("collapsedDuplicates").EnumerateArray()
            .Select(d => (d.GetProperty("id").GetInt64(), string.Join(",", d.GetProperty("pages").EnumerateArray().Select(p => p.GetInt32()))))
            .ToList();
        Assert.Equal([(1025L, "1,2"), (1075L, "3,4"), (1125L, "5,6")], collapsed);
        Assert.Equal(160, extract.GetProperty("recordsReceived").GetInt32());
        Assert.Equal(157, extract.GetProperty("distinctRecords").GetInt32());
        Assert.Equal(extract.GetProperty("distinctRecords").GetInt32(), extract.GetProperty("recordsReceived").GetInt32() - collapsed.Count);
    }

    [Fact]
    [Trait("Scenario", "data-lineage/Refetch reasons")]
    public async Task A_refetched_page_is_listed_with_the_check_that_failed()
    {
        var source = new FakePageSource(page =>
        {
            var result = FakePageSource.Healthy(page);
            return page == 3 && Interlocked.Increment(ref _shortServed) == 1
                ? PageResult.Success(result.Page! with { Records = [.. result.Page!.Records.Take(24)] }, 1)
                : result;
        });
        await using var api = new TestApi { PageSource = source };

        var refetch = Assert.Single((await GetAuditAsync(api)).GetProperty("extract").GetProperty("pageRefetches").EnumerateArray());

        Assert.Equal(3, refetch.GetProperty("page").GetInt32());
        Assert.Equal("holds 24 records, expected at least 25", refetch.GetProperty("reason").GetString());
    }

    private static int _shortServed;

    [Fact]
    [Trait("Scenario", "data-lineage/Manifest for a run")]
    public async Task Each_run_logs_exactly_one_manifest_success_or_failure()
    {
        var failing = false;
        var source = new FakePageSource(page => Volatile.Read(ref failing)
            ? FakePageSource.Fail(FailureKinds.UpstreamUnavailable, page)
            : FakePageSource.Healthy(page));
        await using var api = new TestApi { PageSource = source };
        var client = api.CreateClient();

        (await client.GetAsync(Route, Ct)).Dispose();
        Volatile.Write(ref failing, true);
        (await client.GetAsync(Route, Ct)).Dispose();

        var manifests = api.Logs.GetSnapshot().Where(r => r.Id.Name is "RunManifest" or "RunManifestWithoutData").ToList();
        Assert.Equal(2, manifests.Count);
        var success = manifests[0];
        Assert.Equal("success", success.GetStructuredStateValue("Outcome"));
        Assert.StartsWith("sha256:", success.GetStructuredStateValue("Fingerprint"), StringComparison.Ordinal);
        Assert.Equal("157", success.GetStructuredStateValue("DistinctRecords"));
        Assert.Equal("142", success.GetStructuredStateValue("RecordsOut"));
        Assert.Matches("^[0-9a-f]{32}$", success.GetStructuredStateValue("TraceId"));
        var failure = manifests[1];
        Assert.Equal("RunManifestWithoutData", failure.Id.Name);
        Assert.Equal(FailureKinds.UpstreamUnavailable, failure.GetStructuredStateValue("Outcome"));
    }

    [Fact]
    [Trait("Scenario", "data-lineage/Data-quality counters")]
    public async Task Rejects_by_reason_and_collapsed_duplicates_are_counted()
    {
        await using var api = WithFakeUpstream(out var upstream);
        upstream.Records[98]["price"] = -4.5m;
        var meters = api.Services.GetRequiredService<IMeterFactory>();
        using var rejected = new MetricCollector<long>(meters, InventoryTelemetry.Name, "inventory.records.rejected");
        using var collapsed = new MetricCollector<long>(meters, InventoryTelemetry.Name, "inventory.records.duplicates_collapsed");
        using var normalized = new MetricCollector<long>(meters, InventoryTelemetry.Name, "inventory.records.spellings_normalized");

        using var response = await api.CreateClient().GetAsync(Route, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reject = Assert.Single(rejected.GetMeasurementSnapshot());
        Assert.Equal(1, reject.Value);
        Assert.Equal("price is negative", reject.Tags["inventory.rejection.reason"]);
        Assert.Equal(3, collapsed.GetMeasurementSnapshot().Sum(m => m.Value));
        Assert.True(normalized.GetMeasurementSnapshot().Sum(m => m.Value) > 0);
    }

    private static TestApi WithFakeUpstream(out FakeUpstream upstream)
    {
        var fake = new FakeUpstream(TimeProvider.System);
        upstream = fake;
        return new TestApi { Upstream = (request, _, _) => Task.FromResult(fake.Respond(request)) };
    }

    private static async Task<JsonElement> GetAuditAsync(TestApi api)
    {
        var body = await api.CreateClient().GetStringAsync($"{Route}?include=audit", Ct);
        return JsonDocument.Parse(body).RootElement.GetProperty("audit").Clone();
    }
}
