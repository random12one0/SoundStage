using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Soundstage.App.Services;

/// <summary>
/// Global hotkey via Win32 RegisterHotKey. Default Ctrl+Alt+B toggles the A/B bypass from
/// anywhere — comparing processed vs untouched sound is a primary feature.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x5057; // arbitrary app-unique id

    private HwndSource? _source;
    private Action? _callback;
    private bool _registered;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Registers the hotkey (e.g. "Ctrl+Alt+B"). Returns false when the combo is taken.</summary>
    public bool Register(Window window, string hotkeyText, Action callback)
    {
        Unregister();
        if (!TryParse(hotkeyText, out var modifiers, out var key))
        {
            return false;
        }

        var helper = new WindowInteropHelper(window);
        var handle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        if (_source is null)
        {
            return false;
        }

        _callback = callback;
        _source.AddHook(WndProc);
        _registered = RegisterHotKey(handle, HotkeyId, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        return _registered;
    }

    public void Unregister()
    {
        if (_source is not null)
        {
            if (_registered)
            {
                UnregisterHotKey(_source.Handle, HotkeyId);
                _registered = false;
            }

            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _callback?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public static bool TryParse(string text, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= 0x0002;
                    break;
                case "alt":
                    modifiers |= 0x0001;
                    break;
                case "shift":
                    modifiers |= 0x0004;
                    break;
                case "win":
                    modifiers |= 0x0008;
                    break;
                default:
                    if (!Enum.TryParse(raw, ignoreCase: true, out key))
                    {
                        return false;
                    }

                    break;
            }
        }

        return key != Key.None;
    }

    public void Dispose() => Unregister();
}
