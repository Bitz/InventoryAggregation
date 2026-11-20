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
/// trigger and their duration, upstream retries, page refetches, re-collections and the stored report's
/// age. Nothing here carries the upstream address or the key.
/// </summary>
public sealed class InventoryMetrics
{
    private readonly Counter<long> _collections;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _refetches;
    private readonly Counter<long> _recollections;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _duplicates;
    private readonly Counter<long> _normalized;
    private Func<double?>? _reportAge;

    public InventoryMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(InventoryTelemetry.Name);
        _collections = meter.CreateCounter<long>("inventory.collections", description: "Collections finished, by outcome (success or the failure kind) and trigger (startup, scheduled or fresh).");
        _duration = meter.CreateHistogram<double>("inventory.collection.duration", unit: "s", description: "Time from the start of a collection to its outcome.");
        _retries = meter.CreateCounter<long>("inventory.upstream.retries", description: "Upstream requests retried, by what caused the retry.");
        _refetches = meter.CreateCounter<long>("inventory.collection.page_refetches", description: "Pages fetched again because they disagreed or did not parse.");
        _recollections = meter.CreateCounter<long>("inventory.collection.recollections", description: "Full collections repeated because the first did not reconcile.");
        _rejected = meter.CreateCounter<long>("inventory.records.rejected", description: "Rejection reasons found by validation, one per reason.");
        _duplicates = meter.CreateCounter<long>("inventory.records.duplicates_collapsed", description: "Ids that arrived more than once with identical content and were kept once.");
        _normalized = meter.CreateCounter<long>("inventory.records.spellings_normalized", description: "Accepted records whose category spelling differed from the name it is shown under.");
        meter.CreateObservableGauge("inventory.report.age", ObserveReportAge, unit: "s", description: "Seconds since the stored report was made; nothing while no report is stored.");
    }

    public void CollectionFinished(string outcome, string trigger, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "inventory.outcome", outcome },
            { "inventory.trigger", trigger },
        };
        _collections.Add(1, tags);
        _duration.Record(duration.TotalSeconds, tags);
    }

    /// <summary>Where <c>inventory.report.age</c> reads from: seconds since the stored report was made, or null when there is none.</summary>
    public void TrackReportAge(Func<double?> age) => _reportAge = age;

    public void Retried(string reason) => _retries.Add(1, new KeyValuePair<string, object?>("inventory.retry.reason", reason));

    public void PageRefetched() => _refetches.Add(1);

    public void Recollected() => _recollections.Add(1);

    public void RecordRejected(string reason) => _rejected.Add(1, new KeyValuePair<string, object?>("inventory.rejection.reason", reason));

    public void DuplicatesCollapsed(int count) => _duplicates.Add(count);

    public void SpellingsNormalized(int count) => _normalized.Add(count);

    private IEnumerable<Measurement<double>> ObserveReportAge() =>
        _reportAge?.Invoke() is { } age ? [new Measurement<double>(age)] : [];
}
