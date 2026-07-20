using Soundstage.Core.Abstractions;

namespace Soundstage.Core.Tests.Support;

public sealed class TestClock : IClock
{
    public TestClock(DateTimeOffset? start = null)
    {
        UtcNow = start ?? new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow { get; private set; }

    /// <summary>Local time offset used for schedule tests; default +2h so local != utc.</summary>
    public TimeSpan LocalOffset { get; set; } = TimeSpan.FromHours(2);

    public DateTimeOffset LocalNow => UtcNow.ToOffset(LocalOffset);

    public void Advance(TimeSpan by) => UtcNow += by;

    public void SetLocal(DateTimeOffset local)
    {
        LocalOffset = local.Offset;
        UtcNow = local.ToUniversalTime();
    }
}

/// <summary>Manual <see cref="IDelayScheduler"/>: callbacks fire only when the test says so.</summary>
public sealed class TestScheduler : IDelayScheduler
{
    private sealed class Item : IDisposable
    {
        public required TimeSpan Delay { get; init; }

        public required Action Callback { get; init; }

        public bool Cancelled { get; private set; }

        public bool Fired { get; set; }

        public void Dispose() => Cancelled = true;
    }

    private readonly List<Item> _items = [];

    public int PendingCount => _items.Count(i => i is { Cancelled: false, Fired: false });

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        var item = new Item { Delay = delay, Callback = callback };
        _items.Add(item);
        return item;
    }

    /// <summary>Fires all pending callbacks (in scheduling order).</summary>
    public void FireAll()
    {
        foreach (var item in _items.Where(i => i is { Cancelled: false, Fired: false }).ToList())
        {
            item.Fired = true;
            item.Callback();
        }
    }

    public TimeSpan? LastDelay => _items.Count == 0 ? null : _items[^1].Delay;
}
