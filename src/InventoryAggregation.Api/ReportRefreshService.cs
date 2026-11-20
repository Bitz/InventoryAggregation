using InventoryAggregation.Core.Reporting;

namespace InventoryAggregation.Api;

/// <summary>
/// Runs the <see cref="ReportRefresher"/> schedule for the life of the host. The first refresh starts
/// as the host starts and does not delay it (design.md D27). On shutdown the running collection is
/// cancelled and not recorded as a failure (D33).
/// </summary>
internal sealed class ReportRefreshService(ReportRefresher refresher) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => refresher.RunScheduleAsync(stoppingToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await refresher.ShutdownAsync().WaitAsync(cancellationToken);
        await base.StopAsync(cancellationToken);
    }
}
