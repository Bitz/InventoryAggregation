using InventoryAggregation.Core.Collection;
using InventoryAggregation.Core.Records;
using InventoryAggregation.Core.Tests.Support;
using Microsoft.Extensions.Time.Testing;

namespace InventoryAggregation.Core.Tests.Collection;

public sealed class InventoryCollectorTests
{
    private const int Size = 25;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "inventory-collection/157 records, page size 25")]
    public async Task Fetches_every_reported_page()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(data.Healthy());

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(157, result.Dataset.Records.Count);
        Assert.Equal(Enumerable.Range(1001, 157).Select(i => (long)i), result.Dataset.Records.Select(r => r.Id!.Value));
        for (var page = 1; page <= 7; page++)
        {
            Assert.Equal(1, source.CallsFor(page));
        }
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Concurrency bound")]
    public async Task Never_has_more_than_MaxConcurrency_pages_in_flight()
    {
        var data = new UpstreamData(40 * Size);
        var source = new ScriptedPageSource(async (call, _) =>
        {
            // Hold every call across a yield so that concurrent calls overlap.
            await Task.Yield();
            return ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize));
        });

        var result = await Components.Collector(source, new InventoryReportSettings { MaxConcurrency = 4 }).CollectAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(1000, result.Dataset.Records.Count);
        Assert.Equal(4, source.MaxInFlight);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Empty dataset")]
    public async Task An_empty_dataset_is_valid()
    {
        var source = new ScriptedPageSource(call => ScriptedPageSource.Ok(new InventoryPage(call.Page, call.PageSize, 0, 0, [])));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Dataset.Records);
        Assert.Equal(0, result.Dataset.TotalRecords);
    }

    [Fact]
    public async Task An_empty_dataset_reporting_one_page_is_valid()
    {
        var source = new ScriptedPageSource(call => ScriptedPageSource.Ok(new InventoryPage(call.Page, call.PageSize, 1, 0, [])));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Dataset.Records);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Wrong page returned")]
    public async Task A_page_echoing_another_page_number_fails_the_page_check()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => call.Page == 4
            ? ScriptedPageSource.Ok(data.Page(3, call.PageSize))
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize)));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.InconsistentPaging, result.Failure.Kind);
        Assert.Equal(4, result.Failure.Page);
        Assert.Contains("page is 3, expected 4", result.Failure.Detail, StringComparison.Ordinal);
        Assert.Equal(3, source.CallsFor(4));
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Overlapping page")]
    public async Task A_page_repeating_the_previous_last_record_passes_and_collapses()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(data.Healthy(overlap: true));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(26, data.Page(2, Size, overlap: true).Records.Count);
        Assert.Equal(157, result.Dataset.Records.Count);
        Assert.Contains(1025L, result.Dataset.CollapsedDuplicateIds);
        Assert.Equal(0, result.Dataset.PageRefetches);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Short page")]
    public async Task A_page_with_fewer_records_than_expected_fails()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => call.Page == 3
            ? ScriptedPageSource.Ok(data.Envelope(3, call.PageSize, data.Page(3, call.PageSize).Records.Take(24).ToList()))
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize)));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.InconsistentPaging, result.Failure.Kind);
        Assert.Equal(3, result.Failure.Page);
        Assert.Contains("holds 24 records, expected at least 25", result.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Totals drift")]
    public async Task Drifting_totals_fail_after_refetches_naming_page_field_and_values()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => call.Page == 5
            ? ScriptedPageSource.Ok(data.Page(5, call.PageSize) with { TotalRecords = 160 })
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize)));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.InconsistentPaging, result.Failure.Kind);
        Assert.Equal(5, result.Failure.Page);
        Assert.Contains("Page 5: totalRecords is 160, expected 157", result.Failure.Detail, StringComparison.Ordinal);
        Assert.Equal(3, source.CallsFor(5)); // the first fetch and PageRefetchLimit (2) refetches
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Short page in the middle")]
    public async Task A_short_page_that_refetches_whole_is_used()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => call is { Page: 3, Previous: 0 }
            ? ScriptedPageSource.Ok(data.Envelope(3, call.PageSize, data.Page(3, call.PageSize).Records.Take(24).ToList()))
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize)));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(157, result.Dataset.Records.Count);
        Assert.Equal(2, source.CallsFor(3));
        Assert.Equal(1, result.Dataset.PageRefetches);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Totals understate the data")]
    public async Task Records_past_the_last_page_fail_collection()
    {
        var data = new UpstreamData();
        var extra = new List<RawRecord> { UpstreamData.Record(2001), UpstreamData.Record(2002), UpstreamData.Record(2003) };
        var source = new ScriptedPageSource(call => call.Page == 8
            ? ScriptedPageSource.Ok(data.Envelope(8, call.PageSize, extra))
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize)));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.InconsistentPaging, result.Failure.Kind);
        Assert.Equal(8, result.Failure.Page);
        Assert.Contains("understate", result.Failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Past-the-end returns 400")]
    public async Task A_400_past_the_last_page_confirms_the_end()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => call.Page == 8
            ? ScriptedPageSource.Fail(FailureKinds.UpstreamMisconfigured, 8, status: 400)
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize)));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(157, result.Dataset.Records.Count);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Identical duplicate")]
    public async Task An_identical_duplicate_is_kept_once_and_listed()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call =>
        {
            var page = data.Page(call.Page, call.PageSize);
            return call.Page == 2
                ? ScriptedPageSource.Ok(page with { Records = [data.Records.Single(r => r.Id == 1020), .. page.Records] })
                : ScriptedPageSource.Ok(page);
        });

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Dataset.Records, r => r.Id == 1020);
        Assert.Equal([1020L], result.Dataset.CollapsedDuplicateIds);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Page boundary overlap")]
    public async Task An_overlap_that_hides_a_record_fails_as_incomplete_after_recollection()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => ScriptedPageSource.Ok(OverlapWithOmission(data, call)));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.IncompleteDataset, result.Failure.Kind);
        Assert.Equal(157, result.Failure.ExpectedCount);
        Assert.Equal(156, result.Failure.CollectedCount);
        Assert.Contains(1050L, result.Failure.DuplicateIds);
        Assert.Contains("Expected 157 distinct records, collected 156 after 2 collection attempt(s)", result.Failure.Detail, StringComparison.Ordinal);
        Assert.Equal(2, source.CallsFor(1));
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/A one-off glitch")]
    public async Task A_glitch_in_the_first_pass_is_settled_by_the_second()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => call.Previous == 0
            ? ScriptedPageSource.Ok(OverlapWithOmission(data, call))
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize)));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(157, result.Dataset.Records.Count);
        Assert.Equal(2, result.Dataset.CollectionAttempts);
        Assert.Equal(16, result.Dataset.Requests);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Same id, different content")]
    public async Task A_conflicting_duplicate_fails_after_recollection()
    {
        var data = new UpstreamData();
        var original = data.Records.Single(r => r.Id == 1042);
        var source = new ScriptedPageSource(call =>
        {
            var page = data.Page(call.Page, call.PageSize);
            return call.Page == 4
                ? ScriptedPageSource.Ok(page with { Records = [original with { Quantity = original.Quantity + 5 }, .. page.Records] })
                : ScriptedPageSource.Ok(page);
        });

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.ConflictingRecords, result.Failure.Kind);
        Assert.Equal([1042L], result.Failure.ConflictingIds);
        Assert.Equal(2, source.CallsFor(4));
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Absurd totals")]
    public async Task Absurd_totals_fail_after_one_request()
    {
        var source = new ScriptedPageSource(call =>
            ScriptedPageSource.Ok(new InventoryPage(1, call.PageSize, 5_000_000, 125_000_000, [.. Enumerable.Range(1, 25).Select(i => UpstreamData.Record(i))])));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.DatasetTooLarge, result.Failure.Kind);
        Assert.Single(source.Calls);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Caller goes away")]
    public async Task Cancelling_stops_every_request_in_flight_and_starts_no_more()
    {
        var data = new UpstreamData();
        var cancelledInFlight = 0;
        var source = new ScriptedPageSource(async (call, token) =>
        {
            if (call.Page > 1)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelledInFlight);
                    throw;
                }
            }

            return ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize));
        });
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var collecting = Components.Collector(source).CollectAsync(caller.Token);
        Assert.Equal(5, source.Calls.Count); // page 1 plus MaxConcurrency (4) pages waiting
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collecting);
        Assert.Equal(4, cancelledInFlight);
        Assert.Equal(5, source.Calls.Count);
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/One page never succeeds")]
    public async Task A_page_source_failure_ends_collection_without_records()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => call.Page == 6
            ? ScriptedPageSource.Fail(FailureKinds.UpstreamUnavailable, 6, status: 503, attempts: 4)
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize)));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Dataset);
        Assert.Equal(FailureKinds.UpstreamUnavailable, result.Failure.Kind);
        Assert.Equal(6, result.Failure.Page);
        Assert.Equal(1, source.CallsFor(6)); // the source already retried; the collector does not
    }

    [Fact]
    [Trait("Scenario", "inventory-collection/Healthy run")]
    public async Task A_healthy_run_reports_eight_requests_no_refetches_one_attempt()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(data.Healthy());

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal(157, result.Dataset.Records.Count);
        Assert.Equal(157, result.Dataset.TotalRecords);
        Assert.Equal(7, result.Dataset.PagesFetched);
        Assert.Equal(8, result.Dataset.Requests);
        Assert.Equal(0, result.Dataset.PageRefetches);
        Assert.Equal(1, result.Dataset.CollectionAttempts);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], source.Calls.Order());
    }

    [Fact]
    public async Task A_malformed_page_is_refetched_and_then_named()
    {
        var data = new UpstreamData();
        var source = new ScriptedPageSource(call => call.Page == 2
            ? ScriptedPageSource.Fail(FailureKinds.MalformedResponse, 2)
            : ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize)));

        var result = await Components.Collector(source).CollectAsync(Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.MalformedResponse, result.Failure.Kind);
        Assert.Equal(3, source.CallsFor(2));
    }

    [Fact]
    public async Task Exceeding_the_collection_timeout_fails_as_upstream_timeout()
    {
        var data = new UpstreamData();
        var time = new FakeTimeProvider();
        var source = new ScriptedPageSource(async (call, token) =>
        {
            if (call.Page == 3)
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return ScriptedPageSource.Ok(data.Page(call.Page, call.PageSize));
        });

        var collecting = Components.Collector(source, time: time).CollectAsync(Ct);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(collecting.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        var result = await collecting;

        Assert.False(result.IsSuccess);
        Assert.Equal(FailureKinds.UpstreamTimeout, result.Failure.Kind);
    }

    /// <summary>Page 3 repeats page 2's last record (1050) in place of its own last record (1075).</summary>
    private static InventoryPage OverlapWithOmission(UpstreamData data, ScriptedCall call)
    {
        var page = data.Page(call.Page, call.PageSize);
        return call.Page == 3
            ? page with { Records = [data.Records.Single(r => r.Id == 1050), .. page.Records.Take(page.Records.Count - 1)] }
            : page;
    }
}
