using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundstage.App.Services;
using Soundstage.Core.Configio;

namespace Soundstage.App.ViewModels;

/// <summary>
/// The confirm-or-revert countdown shown after a guarded apply. Ignoring it is safe by
/// design: the guard restores the previous known-good config.
/// </summary>
public partial class RevertToastViewModel : ObservableObject
{
    private readonly SoundstageControllerAccessor _controller;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private int _secondsRemaining;

    private IDisposable? _tickHandle;
    private DateTimeOffset _deadline;

    public RevertToastViewModel(AppServices services)
    {
        _controller = new SoundstageControllerAccessor(services);
        var guard = services.Orchestrator?.Guard;
        if (guard is null)
        {
            return;
        }

        guard.PendingStarted += () => UiDispatch.Post(() => OnPendingStarted(guard));
        guard.Resolved += outcome => UiDispatch.Post(() =>
        {
            if (outcome != RevertOutcome.Superseded)
            {
                Hide();
            }
        });
    }

    private void OnPendingStarted(RevertGuard guard)
    {
        _deadline = DateTimeOffset.UtcNow + guard.PendingTimeout;
        IsVisible = true;
        Tick();
    }

    private void Tick()
    {
        var remaining = _deadline - DateTimeOffset.UtcNow;
        SecondsRemaining = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        if (!IsVisible || remaining <= TimeSpan.Zero)
        {
            return;
        }

        _tickHandle?.Dispose();
        _tickHandle = _controller.Services.Scheduler.Schedule(TimeSpan.FromMilliseconds(250), () => UiDispatch.Post(Tick));
    }

    private void Hide()
    {
        IsVisible = false;
        _tickHandle?.Dispose();
        _tickHandle = null;
    }

    [RelayCommand]
    private void Keep() => _controller.Services.Controller?.ConfirmPendingApply();

    [RelayCommand]
    private void RevertNow() => _controller.Services.Orchestrator?.Guard.RevertNow();

    /// <summary>Thin wrapper so the toast can exist before the controller does.</summary>
    public sealed record SoundstageControllerAccessor(AppServices Services);
}
