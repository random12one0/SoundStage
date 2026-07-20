using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Soundstage.App.ViewModels;
using Wpf.Ui.Controls;

namespace Soundstage.App;

public partial class MainWindow : FluentWindow
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _statusTimer;

    public ShellViewModel Shell { get; }

    public bool AllowClose { get; set; }

    public MainWindow(AppServices services)
    {
        _services = services;
        Shell = new ShellViewModel(services);
        DataContext = Shell;
        InitializeComponent();

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _statusTimer.Tick += (_, _) => Shell.StatusBar.Refresh();
        _statusTimer.Start();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var minimizeToTray = _services.Controller?.State.Settings.MinimizeToTray ?? true;
        if (!AllowClose && minimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _statusTimer.Stop();
        base.OnClosing(e);
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }
}
