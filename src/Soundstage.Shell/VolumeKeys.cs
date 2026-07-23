using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Soundstage.Shell;

/// <summary>
/// Takes over the keyboard's volume keys while Soundstage is processing.
///
/// This isn't rudeness, it's the only thing that makes sense here. Once your apps play into the
/// outlet, the Windows volume slider is adjusting the outlet's endpoint — and a virtual cable's
/// loopback is taken before that volume is applied, so moving it changes nothing you can hear.
/// Measured on this machine: outlet volume at 100% and at 30% produce an identical output level.
/// The volume keys would otherwise be dead controls.
///
/// So while the engine is running we claim them and drive Soundstage's own volume; the moment it
/// stops, we hand them straight back to Windows.
/// </summary>
public sealed class VolumeKeys : IDisposable
{
    private const int WmHotkey = 0x0312;

    private const int IdUp = 0xB001;
    private const int IdDown = 0xB002;
    private const int IdMute = 0xB003;

    private const uint VkVolumeMute = 0xAD;
    private const uint VkVolumeDown = 0xAE;
    private const uint VkVolumeUp = 0xAF;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    /// <summary>Raised with +1 / -1 for the volume keys, or 0 for mute.</summary>
    public event Action<int>? Pressed;

    public VolumeKeys(System.Windows.Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(_hwnd) ?? throw new InvalidOperationException("no window source");
        _source.AddHook(Hook);
    }

    public bool Enabled
    {
        get => _registered;
        set
        {
            if (value) { Register(); } else { Unregister(); }
        }
    }

    private void Register()
    {
        if (_registered || _disposed)
        {
            return;
        }

        // MOD_NOREPEAT is deliberately NOT set: holding the key should keep changing the volume.
        bool ok = RegisterHotKey(_hwnd, IdUp, 0, VkVolumeUp)
                  & RegisterHotKey(_hwnd, IdDown, 0, VkVolumeDown)
                  & RegisterHotKey(_hwnd, IdMute, 0, VkVolumeMute);

        // A partial failure (another app already owns one of them) still leaves the rest working.
        _registered = ok;
    }

    private void Unregister()
    {
        if (!_registered)
        {
            return;
        }

        UnregisterHotKey(_hwnd, IdUp);
        UnregisterHotKey(_hwnd, IdDown);
        UnregisterHotKey(_hwnd, IdMute);
        _registered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        int id = wParam.ToInt32();
        int delta = id switch
        {
            IdUp => 1,
            IdDown => -1,
            IdMute => 0,
            _ => int.MinValue,
        };

        if (delta == int.MinValue)
        {
            return IntPtr.Zero;
        }

        handled = true;
        Pressed?.Invoke(delta);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        try { _source.RemoveHook(Hook); } catch { /* window already gone */ }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
