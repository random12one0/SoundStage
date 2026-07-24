using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

using Microsoft.Web.WebView2.Core;

namespace Soundstage.Shell;

/// <summary>
/// The v1.0 app window: a frameless host that renders our approved HTML/CSS/JS UI in WebView2, so the
/// exact design we agreed on runs natively. The UI's own title bar drives the window (min / max /
/// close / drag) and its controls drive the real engine — both through a small JS→C# bridge that hands
/// control messages to the <see cref="Engine.EngineController"/>.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Engine.EngineController _controller;

    private TrayIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        _controller = new Engine.EngineController(NotifyUi);
        Loaded += OnLoaded;
        SetUpTray();
    }

    private void SetUpTray()
    {
        try
        {
            _tray = new TrayIcon();
            _tray.ShowRequested += RestoreFromTray;
            _tray.PowerToggled += on => NotifyUi("{\"t\":\"tray-power\",\"on\":" + (on ? "true" : "false") + "}");
            _tray.ExitRequested += () =>
            {
                _reallyExiting = true;
                Close();
            };
        }
        catch
        {
            // No notification area (rare, but possible on stripped-down systems) — run without it.
            _tray = null;
        }
    }

    private VolumeKeys? _volumeKeys;
    private bool _volumeKeysWanted = true;
    private bool _powerOn;

    /// <summary>
    /// Claim the keyboard's volume keys. They're dead controls otherwise — the Windows slider adjusts
    /// the outlet endpoint, and a virtual cable's loopback is taken before that volume applies.
    /// </summary>
    private void SetUpVolumeKeys()
    {
        if (_volumeKeys is not null)
        {
            return;
        }

        try
        {
            _volumeKeys = new VolumeKeys(this);
            _volumeKeys.Pressed += delta => NotifyUi(
                "{\"t\":\"volumekey\",\"delta\":" + delta.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
        }
        catch
        {
            _volumeKeys = null;   // no hotkeys available; the in-app dial still works
        }
    }

    private void HideToTray()
    {
        Hide();
        _tray?.Notify("Soundstage is still running",
                      "Your sound keeps being processed. Double-click the tray icon to bring it back.");
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        });
    }

    // Lets the engine controller send a status back to the page (e.g. "no-cable"). Called on the UI
    // thread from message handling; posts to the page if it has finished loading.
    private void NotifyUi(string message)
    {
        void Post()
        {
            try { Web.CoreWebView2?.PostWebMessageAsString(message); }
            catch { /* page not ready yet */ }
        }

        if (Dispatcher.CheckAccess()) { Post(); } else { Dispatcher.BeginInvoke(Post); }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await Web.EnsureCoreWebView2Async();
        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;

        // Without this, CSS `-webkit-app-region: drag` on the title bar does nothing — which is why
        // dragging the window silently failed. It has to be set before the page loads.
        TryEnableNonClientRegions(core);

        core.WebMessageReceived += OnWebMessage;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
        core.NavigateToString(LoadUi());
    }

    // IsNonClientRegionSupportEnabled lives on a newer settings interface than the one we compile
    // against is guaranteed to expose, so ask for it at runtime: on a current WebView2 runtime the
    // title bar becomes a real caption, and on an older one we fall back to host-side DragMove.
    private bool _nonClientRegionsEnabled;

    private void TryEnableNonClientRegions(CoreWebView2 core)
    {
        try
        {
            object settings = core.Settings;
            var prop = settings.GetType().GetProperty("IsNonClientRegionSupportEnabled");
            if (prop?.CanWrite == true)
            {
                prop.SetValue(settings, true);
                _nonClientRegionsEnabled = true;
            }
        }
        catch
        {
            _nonClientRegionsEnabled = false;
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }

        // Control messages from the UI are JSON; window commands are bare strings. A few JSON types
        // belong to the window rather than the engine (updates, the tray), so peek before forwarding.
        if (message.StartsWith('{'))
        {
            if (!TryHandleWindowMessage(message))
            {
                _controller.HandleMessage(message);
            }

            return;
        }

        switch (message)
        {
            case "min":
                WindowState = WindowState.Minimized;
                break;
            case "max":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;
            case "close":
                if (_closeToTray) { HideToTray(); } else { _reallyExiting = true; Close(); }
                break;
            case "drag":
                StartHostDrag();
                break;
            case "ready":
                SendInitialState();
                StartLevelMeter();
                SetUpVolumeKeys();
                break;
        }
    }

    /// <summary>
    /// Fallback window drag for runtimes without non-client region support. WPF's DragMove needs the
    /// button to still be down, and by the time a WebView2 message arrives it may not be — so this
    /// asks the OS to run its own move loop instead, which behaves correctly either way.
    /// </summary>
    private void StartHostDrag()
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
        }

        try
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
                return;
            }
        }
        catch { /* mouse already released — fall through */ }

        try
        {
            var helper = new WindowInteropHelper(this);
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(helper.Handle, NativeMethods.WmSysCommand,
                                      (IntPtr)NativeMethods.ScMoveByCaption, IntPtr.Zero);
        }
        catch { /* nothing sensible left to try */ }
    }

    /// <summary>Hand the page everything it needs to render the real machine: the saved settings and
    /// the actual playback devices. Sent once, when the page says it's ready.</summary>
    private void SendInitialState()
    {
        string? saved = Engine.AppState.Load();
        var payload = new
        {
            t = "init",
            state = saved,
            runAtLogin = Engine.Startup.IsRunAtLogin(),
            engine = _controller.EngineAvailable,
            dragMode = _nonClientRegionsEnabled ? "native" : "host",
            version = Engine.Updater.CurrentVersion,
            statePath = Engine.AppState.FilePath,
        };

        NotifyUi(JsonSerializer.Serialize(payload));
        _controller.SendDeviceList();
    }

    // ---- messages the window owns rather than the engine ----------------------------------------

    private bool _closeToTray = true;
    private bool _reallyExiting;

    /// <summary>Returns true if this message was for the window and has been handled.</summary>
    private bool TryHandleWindowMessage(string json)
    {
        string type;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch { return false; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("t", out JsonElement t) || t.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            type = t.GetString() ?? "";

            switch (type)
            {
                case "update-check":
                    _ = CheckForUpdateAsync(silent: false);
                    return true;

                case "update-download":
                    {
                        string url = doc.RootElement.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
                        _ = DownloadUpdateAsync(url);
                        return true;
                    }

                case "open-url":
                    {
                        string url = doc.RootElement.TryGetProperty("url", out JsonElement u) ? u.GetString() ?? "" : "";
                        Engine.Updater.OpenPage(url);
                        return true;
                    }

                case "tray":
                    _closeToTray = doc.RootElement.TryGetProperty("closeToTray", out JsonElement c) &&
                                   c.ValueKind != JsonValueKind.False;
                    return true;

                case "hide":
                    HideToTray();
                    return true;

                case "powerstate":
                    {
                        bool on = doc.RootElement.TryGetProperty("on", out JsonElement p) &&
                                  p.ValueKind == JsonValueKind.True;
                        _powerOn = on;
                        _tray?.SetPower(on);

                        // Only hold the volume keys while we're actually in the audio path; hand
                        // them back to Windows the moment we're not.
                        if (_volumeKeys is not null && _volumeKeysWanted) { _volumeKeys.Enabled = on; }
                        return false;   // the engine wants to see this one too
                    }

                case "volumekeys":
                    _volumeKeysWanted = doc.RootElement.TryGetProperty("on", out JsonElement vk) &&
                                        vk.ValueKind != JsonValueKind.False;
                    if (_volumeKeys is not null) { _volumeKeys.Enabled = _volumeKeysWanted && _powerOn; }
                    return true;
            }
        }

        return false;
    }

    private async Task CheckForUpdateAsync(bool silent)
    {
        Engine.Updater.Result r = await Engine.Updater.CheckAsync();
        NotifyUi(JsonSerializer.Serialize(new
        {
            t = "update",
            available = r.Available,
            current = r.Current,
            latest = r.Latest,
            notes = r.Notes,
            url = r.Url,
            asset = r.AssetUrl,
            silent,
        }));

        if (r.Available && silent)
        {
            _tray?.Notify("Soundstage " + r.Latest + " is available", "Open Soundstage to install it.");
        }
    }

    private async Task DownloadUpdateAsync(string url)
    {
        var progress = new Progress<int>(p => NotifyUi(
            JsonSerializer.Serialize(new { t = "update-progress", percent = p })));

        string? file = await Engine.Updater.DownloadAsync(url, progress);
        NotifyUi(JsonSerializer.Serialize(new { t = "update-done", ok = file is not null, path = file ?? "" }));
        if (file is not null)
        {
            Engine.Updater.Reveal(file);
        }
    }

    /// <summary>
    /// Feed the status strip's level meter. This is the one honest answer to "is it actually doing
    /// anything?" — the bars only move when real audio is flowing through the engine.
    /// </summary>
    private void StartLevelMeter()
    {
        if (_meterTimer is not null)
        {
            return;
        }

        _meterTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _meterTimer.Tick += (_, _) =>
        {
            // Now-playing (the song title) comes from Windows' media session, not from our audio
            // path, so it must be polled whichever mode we're in — otherwise plugin mode shows
            // "nothing playing" while Spotify is clearly running.
            PollNowPlaying();

            if (!_controller.IsRunning)
            {
                // The app isn't capturing — but the PLUGIN might be doing the work. Ask it for live
                // meters. This is what keeps the level and per-speaker meters alive in plugin mode.
                var pm = _controller.PollPluginMeters();
                if (pm is { } m)
                {
                    // Audio just started flowing through the plugin? Re-assert our full settings, in
                    // case the stream locked before the app finished publishing (music started during
                    // launch). Cheap — once per playback start — and it self-heals any missed update.
                    if (!_pluginWasActive)
                    {
                        _pluginWasActive = true;
                        _controller.RepublishToPlugin();
                    }

                    _meterWasLive = true;
                    var chJson2 = new System.Text.StringBuilder("[");
                    for (int c = 0; c < m.Channels.Count; c++)
                    {
                        if (c > 0) { chJson2.Append(','); }
                        chJson2.Append(m.Channels[c].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                    }

                    chJson2.Append(']');

                    // The source layout is what the plugin inferred from the actual audio (2 for
                    // stereo, full width for real surround) — not a hardcoded 2, which is what made
                    // the badge always read "2.0" no matter what was playing.
                    int srcCh = m.SourceChannels > 0 ? m.SourceChannels : 2;
                    NotifyUi(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{{\"t\":\"level\",\"in\":{m.Out:0.####},\"out\":{m.Out:0.####}," +
                        $"\"inCh\":{srcCh},\"outCh\":{m.ChannelCount}," +
                        $"\"ch\":{chJson2},\"live\":true,\"plugin\":true}}"));
                    return;
                }

                _pluginWasActive = false;   // stream ended; re-assert settings when the next one starts

                // No plugin in the path? Meter passively from Windows' loopback — the safe way to
                // watch the speakers that can't touch the audio. This is the primary meter source
                // now that the plugin is optional.
                var nm = _controller.PollNativeMeters();
                if (nm is { } n)
                {
                    _meterWasLive = true;
                    float peak = 0f;
                    var chJson3 = new System.Text.StringBuilder("[");
                    for (int c = 0; c < n.Channels.Count; c++)
                    {
                        if (c > 0) { chJson3.Append(','); }
                        chJson3.Append(n.Channels[c].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                        if (n.Channels[c] > peak) { peak = n.Channels[c]; }
                    }

                    chJson3.Append(']');

                    // Source layout inferred from whether the non-front channels carry signal.
                    int srcCh2 = 2;
                    for (int c = 2; c < n.Channels.Count; c++)
                    {
                        if (n.Channels[c] > 0.004f) { srcCh2 = n.ChannelCount; break; }
                    }

                    NotifyUi(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                        $"{{\"t\":\"level\",\"in\":{peak:0.####},\"out\":{peak:0.####}," +
                        $"\"inCh\":{srcCh2},\"outCh\":{n.ChannelCount}," +
                        $"\"ch\":{chJson3},\"live\":true,\"native\":true}}"));
                    return;
                }

                if (_meterWasLive)
                {
                    _meterWasLive = false;
                    NotifyUi("{\"t\":\"level\",\"in\":0,\"out\":0,\"live\":false}");
                }

                return;
            }

            _meterWasLive = true;
            (float inPeak, float outPeak) = _controller.Levels;
            (int inCh, int outCh) = _controller.ActiveLayouts;
            (int latency, double levelerDb, double limiterDb) = _controller.Meters;
            // Per-speaker levels ride along on the meter tick rather than getting their own timer —
            // it's eight small numbers, and they should move in step with the main meter.
            var chLevels = _controller.ChannelLevels;
            var chJson = new System.Text.StringBuilder("[");
            for (int c = 0; c < chLevels.Count; c++)
            {
                if (c > 0) { chJson.Append(','); }
                chJson.Append(chLevels[c].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            }

            chJson.Append(']');

            NotifyUi(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{{\"t\":\"level\",\"in\":{inPeak:0.####},\"out\":{outPeak:0.####}," +
                $"\"inCh\":{inCh},\"outCh\":{outCh},\"latency\":{latency}," +
                $"\"ch\":{chJson}," +
                $"\"leveler\":{levelerDb:0.##},\"limiter\":{limiterDb:0.##},\"live\":true}}"));
        };
        _meterTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _meterTimer;
    private bool _meterWasLive;
    private bool _pluginWasActive;

    // Now-playing is polled far more slowly than the meter — track changes are a human-speed event,
    // and the media-session API is not free.
    private int _nowPlayingTick;
    private string _lastNowPlaying = "";
    private bool _nowPlayingBusy;

    private void PollNowPlaying()
    {
        if (++_nowPlayingTick < 12 || _nowPlayingBusy)   // ~1s at an 80 ms meter tick
        {
            return;
        }

        _nowPlayingTick = 0;
        _nowPlayingBusy = true;

        _ = Task.Run(async () =>
        {
            Audio.NowPlaying.Track? track = await Audio.NowPlaying.CurrentAsync();
            string payload = JsonSerializer.Serialize(new
            {
                t = "nowplaying",
                title = track?.Title ?? "",
                artist = track?.Artist ?? "",
                app = track?.App ?? "",
                playing = track?.Playing ?? false,
            });

            if (payload != _lastNowPlaying)
            {
                _lastNowPlaying = payload;
                NotifyUi(payload);
            }

            _nowPlayingBusy = false;
        });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // The X on our own title bar routes through OnWebMessage; this catches Alt+F4 and the like,
        // where closing the window would otherwise silently stop processing the user's audio.
        if (!_reallyExiting && _closeToTray && _tray is not null)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _meterTimer?.Stop();
        _volumeKeys?.Dispose();
        _tray?.Dispose();
        _controller.Dispose();
        base.OnClosed(e);
    }

    private static string LoadUi()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Soundstage.Shell.web.index.html")
                           ?? throw new InvalidOperationException("UI resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Runs at document creation: the title-bar buttons post real window commands. Dragging is handled
    // by CSS -webkit-app-region when the runtime supports it; the page falls back to posting 'drag'.
    private const string BridgeScript = @"
window.addEventListener('DOMContentLoaded', function () {
  var post = function (m) { try { window.chrome.webview.postMessage(m); } catch (e) {} };
  var wb = document.querySelectorAll('.tb .wb b');
  if (wb.length >= 3) {
    wb[0].addEventListener('click', function () { post('min'); });
    wb[1].addEventListener('click', function () { post('max'); });
    wb[2].addEventListener('click', function () { post('close'); });
  }
});";

    private static class NativeMethods
    {
        public const int WmSysCommand = 0x0112;

        // SC_MOVE | HTCAPTION — "move this window as if the user grabbed its title bar".
        public const int ScMoveByCaption = 0xF012;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
