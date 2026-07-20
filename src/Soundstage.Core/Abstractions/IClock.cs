namespace Soundstage.Core.Abstractions;

/// <summary>Injectable time source; automations and the revert guard must be simulation-testable.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Local time — schedules ("after 10pm") are evaluated in the machine's local time.</summary>
    DateTimeOffset LocalNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateTimeOffset LocalNow => DateTimeOffset.Now;
}

/// <summary>Injectable delayed-callback scheduler (the revert guard's countdown, automation debounce).</summary>
public interface IDelayScheduler
{
    /// <summary>Schedules <paramref name="callback"/> after <paramref name="delay"/>; disposing the handle cancels.</summary>
    IDisposable Schedule(TimeSpan delay, Action callback);
}

public sealed class ThreadingDelayScheduler : IDelayScheduler
{
    public static readonly ThreadingDelayScheduler Instance = new();

    public IDisposable Schedule(TimeSpan delay, Action callback)
    {
        Timer? timer = null;
        timer = new Timer(_ =>
        {
            timer?.Dispose();
            callback();
        });
        timer.Change(delay, Timeout.InfiniteTimeSpan);
        return timer;
    }
}
