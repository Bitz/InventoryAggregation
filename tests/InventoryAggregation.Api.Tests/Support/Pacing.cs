namespace InventoryAggregation.Api.Tests.Support;

internal static class Pacing
{
    /// <summary>The most requests that fall in any half-open interval of length <paramref name="window"/>, wherever it starts.</summary>
    public static int MaxInAnyWindow(IEnumerable<RecordedRequest> requests, TimeSpan window)
    {
        var times = requests.Select(r => r.At).Order().ToList();
        var max = 0;
        for (var i = 0; i < times.Count; i++)
        {
            max = Math.Max(max, times.Skip(i).TakeWhile(t => t - times[i] < window).Count());
        }

        return max;
    }
}
