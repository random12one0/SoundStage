using Soundstage.Core.Configio;
using Soundstage.Core.Tests.Support;
using Xunit;

namespace Soundstage.Core.Tests.Configio;

public class RevertGuardTests
{
    private readonly TestScheduler _scheduler = new();
    private readonly RevertGuard _guard;
    private readonly List<RevertOutcome> _outcomes = [];
    private int _reverts;

    public RevertGuardTests()
    {
        _guard = new RevertGuard(_scheduler);
        _guard.Resolved += o => _outcomes.Add(o);
    }

    [Fact]
    public void Timeout_FiresRevert_AndReturnsToIdle()
    {
        _guard.Arm(TimeSpan.FromSeconds(10), () => _reverts++);
        Assert.Equal(RevertGuardStatus.Pending, _guard.Status);

        _scheduler.FireAll();

        Assert.Equal(1, _reverts);
        Assert.Equal(RevertGuardStatus.Idle, _guard.Status);
        Assert.Equal([RevertOutcome.RolledBack], _outcomes);
    }

    [Fact]
    public void Confirm_CancelsCountdown_NoRevert()
    {
        _guard.Arm(TimeSpan.FromSeconds(10), () => _reverts++);
        _guard.Confirm();
        _scheduler.FireAll();

        Assert.Equal(0, _reverts);
        Assert.Equal(RevertGuardStatus.Idle, _guard.Status);
        Assert.Equal([RevertOutcome.Confirmed], _outcomes);
    }

    [Fact]
    public void ReArmWhilePending_KeepsOriginalRollbackTarget()
    {
        var restored = "";
        _guard.Arm(TimeSpan.FromSeconds(10), () => restored = "first-known-good");
        _guard.Arm(TimeSpan.FromSeconds(10), () => restored = "second-experiment");

        _scheduler.FireAll();

        Assert.Equal("first-known-good", restored);
        Assert.Contains(RevertOutcome.Superseded, _outcomes);
        Assert.Contains(RevertOutcome.RolledBack, _outcomes);
    }

    [Fact]
    public void ReArm_RestartsTheCountdown()
    {
        _guard.Arm(TimeSpan.FromSeconds(10), () => _reverts++);
        var firstTimerFired = false;
        // Simulate: first timer would have fired but was disposed by the re-arm.
        _guard.Arm(TimeSpan.FromSeconds(10), () => _reverts++);

        Assert.False(firstTimerFired);
        Assert.Equal(1, _scheduler.PendingCount); // old timer cancelled, one live countdown

        _scheduler.FireAll();
        Assert.Equal(1, _reverts);
    }

    [Fact]
    public void ConfirmWhenIdle_IsANoOp()
    {
        _guard.Confirm();
        Assert.Empty(_outcomes);
    }

    [Fact]
    public void TimeoutAfterConfirm_DoesNothing()
    {
        _guard.Arm(TimeSpan.FromSeconds(10), () => _reverts++);
        _guard.Confirm();

        // Even if a stale timer callback slipped through, state guards it.
        _scheduler.FireAll();
        Assert.Equal(0, _reverts);
        Assert.Equal([RevertOutcome.Confirmed], _outcomes);
    }
}
