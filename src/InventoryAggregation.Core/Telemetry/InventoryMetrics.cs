using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace InventoryAggregation.Core.Telemetry;

/// <summary>Names shared by the service's traces and metrics (design.md D25).</summary>
public static class InventoryTelemetry
{
    /// <summary>The activity source and meter name.</summary>
    public const string Name = "InventoryAggregation";

    /// <summary>Spans the service creates itself, such as <c>collect inventory</c>.</summary>
    public static readonly ActivitySource ActivitySource = new(Name);
}

/// <summary>
/// The service's own measurements, on the <c>InventoryAggregation</c> meter: collections by outcome and
/// their duration, upstream retries, page refetches and re-collections. Nothing here carries the
/// upstream address or the key.
/// </summary>
public sealed class InventoryMetrics
{
    private readonly Counter<long> _collections;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _refetches;
    private readonly Counter<long> _recollections;

    public InventoryMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(InventoryTelemetry.Name);
        _collections = meter.CreateCounter<long>("inventory.collections", description: "Collections finished, by outcome: success or the failure kind.");
        _duration = meter.CreateHistogram<double>("inventory.collection.duration", unit: "s", description: "Time from the start of a collection to its outcome.");
        _retries = meter.CreateCounter<long>("inventory.upstream.retries", description: "Upstream requests retried, by what caused the retry.");
        _refetches = meter.CreateCounter<long>("inventory.collection.page_refetches", description: "Pages fetched again because they disagreed or did not parse.");
        _recollections = meter.CreateCounter<long>("inventory.collection.recollections", description: "Full collections repeated because the first did not reconcile.");
    }

    public void CollectionFinished(string outcome, TimeSpan duration)
    {
        var tag = new KeyValuePair<string, object?>("inventory.outcome", outcome);
        _collections.Add(1, tag);
        _duration.Record(duration.TotalSeconds, tag);
    }

    public void Retried(string reason) => _retries.Add(1, new KeyValuePair<string, object?>("inventory.retry.reason", reason));

    public void PageRefetched() => _refetches.Add(1);

    public void Recollected() => _recollections.Add(1);
}
