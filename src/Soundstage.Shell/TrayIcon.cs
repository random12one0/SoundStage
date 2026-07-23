using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Soundstage.Shell;

/// <summary>
/// The notification-area icon, so Soundstage can sit out of the way while it keeps processing your
/// sound. It has to: the whole point of the app is that it runs in the background, and with the
/// CABLE outlet as your default output, quitting it means silence.
///
/// The icon is drawn rather than shipped as a .ico — one less binary asset to keep in sync with the
/// design, and it scales cleanly to whatever size the shell asks for.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _powerItem;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<bool>? PowerToggled;

    private bool _powerOn;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("Open Soundstage");
        open.Click += (_, _) => ShowRequested?.Invoke();
        open.Font = new Font(open.Font, FontStyle.Bold);

        _powerItem = new ToolStripMenuItem("Turn on");
        _powerItem.Click += (_, _) => PowerToggled?.Invoke(!_powerOn);

        var exit = new ToolStripMenuItem("Quit");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(open);
        menu.Items.Add(_powerItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        _icon = new NotifyIcon
        {
            Icon = BuildIcon(false),
            Text = "Soundstage",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    /// <summary>Reflect the power state: the icon lights up while sound is being processed.</summary>
    public void SetPower(bool on)
    {
        if (_powerOn == on)
        {
            return;
        }

        _powerOn = on;
        _powerItem.Text = on ? "Turn off" : "Turn on";
        _icon.Text = on ? "Soundstage — processing" : "Soundstage — off";

        Icon? previous = _icon.Icon;
        _icon.Icon = BuildIcon(on);
        previous?.Dispose();
    }

    public void Notify(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(4000);
        }
        catch
        {
            // Balloon tips are a courtesy, never a requirement.
        }
    }

    /// <summary>
    /// The app icon when it's available, tinted grey while idle so the tray shows at a glance whether
    /// sound is being processed. Falls back to drawing the mark if the resource is missing.
    /// </summary>
    private static Icon BuildIcon(bool lit)
    {
        try
        {
            using System.IO.Stream? s = typeof(TrayIcon).Assembly
                .GetManifestResourceStream("Soundstage.Shell.soundstage.ico");
            if (s is not null)
            {
                using var full = new Icon(s, 32, 32);
                if (lit)
                {
                    return (Icon)full.Clone();
                }

                // Idle: desaturate so "running" is obvious without reading the tooltip.
                using Bitmap src = full.ToBitmap();
                using var dim = new Bitmap(src.Width, src.Height);
                using (var g = Graphics.FromImage(dim))
                {
                    var grey = new System.Drawing.Imaging.ColorMatrix(new[]
                    {
                        new[] { 0.30f, 0.30f, 0.30f, 0f, 0f },
                        new[] { 0.59f, 0.59f, 0.59f, 0f, 0f },
                        new[] { 0.11f, 0.11f, 0.11f, 0f, 0f },
                        new[] { 0f, 0f, 0f, 0.75f, 0f },
                        new[] { 0f, 0f, 0f, 0f, 1f },
                    });
                    using var attrs = new System.Drawing.Imaging.ImageAttributes();
                    attrs.SetColorMatrix(grey);
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height),
                                0, 0, src.Width, src.Height, GraphicsUnit.Pixel, attrs);
                }

                return Icon.FromHandle(dim.GetHicon());
            }
        }
        catch
        {
            // Fall through to the drawn mark.
        }

        return DrawFallbackIcon(lit);
    }

    /// <summary>The waveform mark, drawn small — used if the icon resource isn't there.</summary>
    private static Icon DrawFallbackIcon(bool lit)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color tint = lit ? Color.FromArgb(255, 55, 224, 207) : Color.FromArgb(190, 150, 168, 180);
            using var pen = new Pen(tint, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            // y = a sine that reads as "audio" at 32 px.
            var points = new PointF[24];
            for (int i = 0; i < points.Length; i++)
            {
                float t = i / (float)(points.Length - 1);
                float x = 3 + (t * 26);
                float y = 16 - ((float)Math.Sin(t * Math.PI * 2) * 7.5f);
                points[i] = new PointF(x, y);
            }

            g.DrawCurve(pen, points);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
