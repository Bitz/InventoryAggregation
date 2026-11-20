using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace InventoryAggregation.Core.Tests.Support;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that also knows which timers are pending and when they fall due,
/// so a test can wait until code has parked on a timer before moving the clock past it. Moving a fake
/// clock while code is still setting up its wait would otherwise place the timer late.
/// </summary>
internal sealed class TrackedClock(FakeTimeProvider inner) : TimeProvider
{
    private readonly ConcurrentDictionary<TrackedTimer, DateTimeOffset> _pending = new();

    public FakeTimeProvider Inner { get; } = inner;

    public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();

    public override long GetTimestamp() => Inner.GetTimestamp();

    public override long TimestampFrequency => Inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => Inner.LocalTimeZone;

    /// <summary>A timer exists in the fake clock, not yet fired, that falls due at <paramref name="at"/>.</summary>
    public bool HasTimerDueAt(DateTimeOffset at) => _pending.Values.Contains(at);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new TrackedTimer(this, callback, period);
        lock (timer.Gate)
        {
            // Listed only once the fake clock holds the timer, so a test that sees it listed can move
            // the clock past it and it will fire.
            timer.Inner = Inner.CreateTimer(timer.Fire, state, dueTime, period);
            timer.Track(dueTime, period);
        }

        return timer;
    }

    private sealed class TrackedTimer(TrackedClock owner, TimerCallback callback, TimeSpan period) : ITimer
    {
        private bool _fired;
        private TimeSpan _period = period;

        public Lock Gate { get; } = new();

        public ITimer Inner { get; set; } = null!;

        public void Fire(object? state)
        {
            lock (Gate)
            {
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    _fired = true;
                    owner._pending.TryRemove(this, out _);
                }
            }

            callback(state);
        }

        /// <summary>Called under <see cref="Gate"/>.</summary>
        public void Track(TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            if (_fired || dueTime == Timeout.InfiniteTimeSpan)
            {
                owner._pending.TryRemove(this, out _);
            }
            else
            {
                owner._pending[this] = owner.GetUtcNow() + dueTime;
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (Gate)
            {
                _fired = false;
                var changed = Inner.Change(dueTime, period);
                Track(dueTime, period);
                return changed;
            }
        }

        public void Dispose()
        {
            owner._pending.TryRemove(this, out _);
            Inner.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            owner._pending.TryRemove(this, out _);
            return Inner.DisposeAsync();
        }
    }
}
