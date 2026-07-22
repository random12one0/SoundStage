using System.IO;
using System.Reflection;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Soundstage.Shell;

/// <summary>
/// The v1.0 app window: a frameless host that renders our approved HTML/CSS/JS UI in WebView2, so the
/// exact design we agreed on runs natively. The UI's own title bar drives the window (min / max /
/// close / drag) through a tiny JS→C# bridge. The audio engine is wired in behind this in later steps.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await Web.EnsureCoreWebView2Async();
        var core = Web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.WebMessageReceived += OnWebMessage;

        // Wire the custom title bar (min/max/close + drag) before the page loads.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);

        core.NavigateToString(LoadUi());
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string message;
        try { message = e.TryGetWebMessageAsString(); }
        catch { return; }

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
                if (WindowState == WindowState.Maximized)
                {
                    WindowState = WindowState.Normal;
                }
                try { DragMove(); } catch { /* mouse already released */ }
                break;
        }
    }

    private static string LoadUi()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("Soundstage.Shell.web.index.html")
                           ?? throw new InvalidOperationException("UI resource not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // Runs at document creation: clicks on the title-bar buttons and drags on the bar post messages
    // that OnWebMessage turns into real window commands.
    private const string BridgeScript = @"
window.addEventListener('DOMContentLoaded', function () {
  var post = function (m) { try { window.chrome.webview.postMessage(m); } catch (e) {} };
  var wb = document.querySelectorAll('.tb .wb b');
  if (wb.length >= 3) {
    wb[0].addEventListener('click', function () { post('min'); });
    wb[1].addEventListener('click', function () { post('max'); });
    wb[2].addEventListener('click', function () { post('close'); });
  }
  var tb = document.querySelector('.tb');
  if (tb) {
    tb.style.userSelect = 'none';
    tb.addEventListener('mousedown', function (e) {
      if (e.target.closest('.wb')) { return; }
      post('drag');
    });
  }
});";
}
