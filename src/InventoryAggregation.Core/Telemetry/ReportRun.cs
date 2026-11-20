namespace InventoryAggregation.Core.Telemetry;

/// <summary>
/// The short id of the shared collection the current code runs for, so that progress lines from
/// concurrent reports can be told apart. Flows with the async context into the upstream client.
/// </summary>
public static class ReportRun
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>The id of the collection in progress, or <c>-</c> outside one.</summary>
    public static string Id => Current.Value ?? "-";

    /// <summary>Marks the rest of this async flow as belonging to collection <paramref name="id"/>.</summary>
    public static void Begin(string id) => Current.Value = id;
}
