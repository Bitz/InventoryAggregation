using System.Threading.RateLimiting;

namespace InventoryAggregation.Api.Upstream;

/// <summary>
/// Paces every request to the upstream, retries included, to at most <c>permits</c> per
/// <c>window</c> (design.md D22). One instance is shared by the whole application.
/// </summary>
/// <remarks>
/// <para>
/// A permit is handed back a full window after the request that held it <i>finished</i>, not at a
/// fixed clock boundary. The upstream counts requests in its own fixed windows, whose boundaries we
/// cannot see; a client-side fixed window that straddled one of them could let ten requests land in
/// a single upstream window. Holding each permit for a window after the response means any two
/// requests that share a permit arrive at least a window apart, so no window of that length, however
/// aligned, sees more than <c>permits</c> requests from this service.
/// </para>
/// <para>
/// Waiters are served first come, first served, and stop waiting when their token is cancelled
/// (the caller left, or the page or collection deadline passed). Nothing is ever rejected.
/// </para>
/// </remarks>
public sealed class UpstreamRateLimiter : RateLimiter
{
    private readonly Lock _gate = new();
    private readonly Queue<Waiter> _waiters = new();
    private readonly TimeSpan _window;
    private readonly TimeProvider _timeProvider;
    private int _available;

    public UpstreamRateLimiter(int permits, TimeSpan window, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permits, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _available = permits;
        _window = window;
        _timeProvider = timeProvider;
    }

    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics()
    {
        lock (_gate)
        {
            return new RateLimiterStatistics
            {
                CurrentAvailablePermits = _available,
                CurrentQueuedCount = _waiters.Count,
            };
        }
    }

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ThrowIfNotOne(permitCount);
        lock (_gate)
        {
            if (_available > 0 && _waiters.Count == 0)
            {
                _available--;
                return new Lease(this);
            }
        }

        return Lease.NotAcquired;
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        ThrowIfNotOne(permitCount);
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<RateLimitLease>(cancellationToken);
        }

        Waiter waiter;
        lock (_gate)
        {
            if (_available > 0 && _waiters.Count == 0)
            {
                _available--;
                return ValueTask.FromResult<RateLimitLease>(new Lease(this));
            }

            waiter = new Waiter();
            _waiters.Enqueue(waiter);
        }

        if (cancellationToken.CanBeCanceled)
        {
            waiter.Registration = cancellationToken.UnsafeRegister(
                static (state, token) => ((Waiter)state!).Completion.TrySetCanceled(token),
                waiter);
        }

        return new ValueTask<RateLimitLease>(waiter.Completion.Task);
    }

    private void ReturnLater() => _ = ReturnAfterWindowAsync();

    private async Task ReturnAfterWindowAsync()
    {
        await Task.Delay(_window, _timeProvider).ConfigureAwait(false);

        while (true)
        {
            Waiter? next;
            lock (_gate)
            {
                if (!_waiters.TryDequeue(out next))
                {
                    _available++;
                    return;
                }
            }

            next.Registration.Dispose();

            // A waiter that already gave up passes the permit on to the next one.
            if (next.Completion.TrySetResult(new Lease(this)))
            {
                return;
            }
        }
    }

    private static void ThrowIfNotOne(int permitCount)
    {
        if (permitCount != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(permitCount), permitCount, "Each upstream request takes exactly one permit.");
        }
    }

    private sealed class Waiter
    {
        public TaskCompletionSource<RateLimitLease> Completion { get; } = new();

        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class Lease(UpstreamRateLimiter? owner) : RateLimitLease
    {
        public static readonly Lease NotAcquired = new(null);

        private int _disposed;

        public override bool IsAcquired => owner is not null;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (owner is not null && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReturnLater();
            }

            base.Dispose(disposing);
        }
    }
}
