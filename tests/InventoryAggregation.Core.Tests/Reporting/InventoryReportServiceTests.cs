using InventoryAggregation.Core.Reporting;
using InventoryAggregation.Core.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryAggregation.Core.Tests.Reporting;

public sealed class InventoryReportServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Scenario", "inventory-records/Report policy")]
    public async Task Rejected_records_are_excluded_and_listed_under_the_report_policy()
    {
        var data = new UpstreamData();
        data.Records[98] = data.Records[98] with { Price = -4m }; // id 1099
        await using var services = Components.ReportServices(new ScriptedPageSource(data.Healthy()));

        var result = await services.GetRequiredService<InventoryReportService>().GetReportAsync(Ct);

        Assert.True(result.IsSuccess);
        var rejected = Assert.Single(result.Report.Summary.RejectedRecords);
        Assert.Equal(1099, rejected.Id);
        Assert.Equal(["price is negative"], rejected.Reasons);
        Assert.Equal(156, result.Report.Categories.Sum(c => c.AvailableRecordCount));
        Assert.DoesNotContain(result.Report.Categories.SelectMany(c => c.Items), i => i.Id == 1099);
    }

    [Fact]
    [Trait("Scenario", "inventory-records/Fail policy")]
    public async Task A_rejected_record_fails_the_report_under_the_fail_policy()
    {
        var data = new UpstreamData();
        data.Records[98] = data.Records[98] with { Price = -4m };
        await using var services = Components.ReportServices(
            new ScriptedPageSource(data.Healthy()),
            new InventoryReportSettings { InvalidRecordPolicy = InvalidRecordPolicy.Fail });

        var result = await services.GetRequiredService<InventoryReportService>().GetReportAsync(Ct);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Report);
        Assert.Equal(FailureKinds.InvalidRecords, result.Failure.Kind);
        Assert.Equal(1099, Assert.Single(result.Failure.RejectedRecords).Id);
    }

    [Fact]
    public async Task The_summary_accounts_for_every_record()
    {
        var data = new UpstreamData();
        for (var i = 0; i < 21; i++)
        {
            data.Records[i * 7] = data.Records[i * 7] with { Quantity = 0 };
        }

        data.Records[3] = data.Records[3] with { LastUpdated = "2026-10-15T12:30:00" };
        data.Records[5] = data.Records[5] with { Name = " " };
        await using var services = Components.ReportServices(new ScriptedPageSource(data.Healthy()));

        var result = await services.GetRequiredService<InventoryReportService>().GetReportAsync(Ct);

        Assert.True(result.IsSuccess);
        var summary = result.Report.Summary;
        Assert.Equal(157, summary.TotalRecords);
        Assert.Equal(21, summary.ZeroQuantityExcluded);
        Assert.Single(summary.RejectedRecords);
        Assert.Equal(135, summary.AvailableRecords);
        Assert.Equal(summary.TotalRecords, summary.AvailableRecords + summary.ZeroQuantityExcluded + summary.RejectedRecords.Count);
        Assert.Equal(1, summary.AssumedUtcTimestamps);
        Assert.Equal(7, summary.PagesFetched);
        Assert.Equal(8, summary.Requests);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 15, 4, 5, TimeSpan.Zero), summary.GeneratedAt);
    }
}
