using System.Windows;

namespace Soundstage.App.Services;

/// <summary>
/// Marshals callbacks from audio/timer threads onto the UI thread. Kept as a delegate so
/// viewmodels are constructible in tests without a WPF dispatcher.
/// </summary>
public static class UiDispatch
{
    /// <summary>Replaceable for tests; defaults to the WPF dispatcher when available, else inline.</summary>
    public static Action<Action> Post { get; set; } = action =>
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    };
}
