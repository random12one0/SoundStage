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

    public MainWindow()
    {
        InitializeComponent();
        _controller = new Engine.EngineController(NotifyUi);
        Loaded += OnLoaded;
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

        // Control messages from the UI are JSON; window commands are bare strings.
        if (message.StartsWith('{'))
        {
            _controller.HandleMessage(message);
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
                Close();
                break;
            case "drag":
                StartHostDrag();
                break;
            case "ready":
                SendInitialState();
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
        };

        NotifyUi(JsonSerializer.Serialize(payload));
        _controller.SendDeviceList();
    }

    protected override void OnClosed(EventArgs e)
    {
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
