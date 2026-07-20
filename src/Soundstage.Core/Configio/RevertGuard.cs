using Soundstage.Core.Abstractions;

namespace Soundstage.Core.Configio;

public enum RevertGuardStatus
{
    Idle,
    Pending,
}

public enum RevertOutcome
{
    Confirmed,
    RolledBack,
    Superseded,
}

/// <summary>
/// The confirm-or-revert state machine. After a risky apply, a countdown runs; if the user
/// does not confirm in time, the revert action restores the previous known-good config —
/// so a bad EQ can never leave the system silent with no obvious cause.
///
/// If another guarded apply arrives while pending, the ORIGINAL rollback target is kept:
/// timing out a chain of experiments returns to the last state the user actually confirmed.
/// </summary>
public sealed class RevertGuard
{
    private readonly IDelayScheduler _scheduler;
    private readonly object _gate = new();

    private IDisposable? _timerHandle;
    private Action? _revertAction;

    public RevertGuard(IDelayScheduler scheduler)
    {
        _scheduler = scheduler;
    }

    public RevertGuardStatus Status { get; private set; } = RevertGuardStatus.Idle;

    /// <summary>Deadline info for UI countdowns.</summary>
    public TimeSpan PendingTimeout { get; private set; }

    public event Action? PendingStarted;

    public event Action<RevertOutcome>? Resolved;

    /// <summary>
    /// Arms (or re-arms) the guard. <paramref name="revertAction"/> restores the previous
    /// known-good state; it is retained across re-arms until confirm/rollback.
    /// </summary>
    public void Arm(TimeSpan timeout, Action revertAction)
    {
        bool started;
        lock (_gate)
        {
            _timerHandle?.Dispose();
            if (Status == RevertGuardStatus.Pending)
            {
                // Keep the original rollback target; just restart the countdown.
                started = false;
            }
            else
            {
                _revertAction = revertAction;
                Status = RevertGuardStatus.Pending;
                started = true;
            }

            PendingTimeout = timeout;
            _timerHandle = _scheduler.Schedule(timeout, OnTimeout);
        }

        if (started)
        {
            PendingStarted?.Invoke();
        }
        else
        {
            Resolved?.Invoke(RevertOutcome.Superseded);
            PendingStarted?.Invoke();
        }
    }

    /// <summary>User confirmed the new config; the pending revert is discarded.</summary>
    public void Confirm()
    {
        lock (_gate)
        {
            if (Status != RevertGuardStatus.Pending)
            {
                return;
            }

            _timerHandle?.Dispose();
            _timerHandle = null;
            _revertAction = null;
            Status = RevertGuardStatus.Idle;
        }

        Resolved?.Invoke(RevertOutcome.Confirmed);
    }

    /// <summary>User chose "revert now" instead of waiting out the countdown.</summary>
    public void RevertNow() => OnTimeout();

    private void OnTimeout()
    {
        Action? revert;
        lock (_gate)
        {
            if (Status != RevertGuardStatus.Pending)
            {
                return;
            }

            revert = _revertAction;
            _timerHandle = null;
            _revertAction = null;
            Status = RevertGuardStatus.Idle;
        }

        revert?.Invoke();
        Resolved?.Invoke(RevertOutcome.RolledBack);
    }
}
