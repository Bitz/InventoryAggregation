using System.Collections.Concurrent;
using InventoryAggregation.Core;
using InventoryAggregation.Core.Collection;

namespace InventoryAggregation.Core.Tests.Support;

/// <summary>
/// An in-memory <see cref="IInventoryPageSource"/> driven by a script: for each call it receives the
/// page, the page size and how many times that page has been asked for before (0 on the first call).
/// It records every call and the highest number of calls in flight at once.
/// </summary>
internal sealed class ScriptedPageSource(Func<ScriptedCall, CancellationToken, Task<PageResult>> script) : IInventoryPageSource
{
    private readonly ConcurrentDictionary<int, int> _callsPerPage = new();
    private readonly ConcurrentQueue<int> _calls = new();
    private int _inFlight;
    private int _maxInFlight;

    public ScriptedPageSource(Func<ScriptedCall, PageResult> script)
        : this((call, _) => Task.FromResult(script(call)))
    {
    }

    public IReadOnlyList<int> Calls => [.. _calls];

    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public int CallsFor(int page) => _callsPerPage.GetValueOrDefault(page);

    public async Task<PageResult> GetPageAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var previous = _callsPerPage.AddOrUpdate(page, 1, (_, n) => n + 1) - 1;
        _calls.Enqueue(page);

        var now = Interlocked.Increment(ref _inFlight);
        int seen;
        while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
        {
        }

        try
        {
            return await script(new ScriptedCall(page, pageSize, previous), cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    public static PageResult Ok(InventoryPage page) => PageResult.Success(page, attempts: 1);

    public static PageResult Fail(string kind, int page, int? status = null, int attempts = 1) =>
        PageResult.Failed(new InventoryFailure(kind, $"scripted {kind} for page {page}")
        {
            Page = page,
            StatusCode = status,
            Attempts = attempts,
        });
}

/// <param name="Page">The page asked for.</param>
/// <param name="PageSize">The page size asked for.</param>
/// <param name="Previous">How many times this page was asked for before this call.</param>
internal sealed record ScriptedCall(int Page, int PageSize, int Previous);
