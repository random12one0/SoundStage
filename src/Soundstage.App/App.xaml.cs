using System.Windows;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Soundstage.App.Services;

namespace Soundstage.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private AppServices? _services;
    private MainWindow? _mainWindow;
    private TaskbarIcon? _trayIcon;
    private HotkeyService? _hotkey;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\Soundstage-SingleInstance", out var createdNew);
        if (!createdNew)
        {
            // Another Soundstage is running; bow out quietly.
            Shutdown();
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _services = AppServices.Build();
        try
        {
            _services.Controller?.Initialize();
        }
        catch
        {
            // A failed initial apply (e.g. config dir became read-only) must not stop the
            // app from opening — Diagnostics will explain what's wrong.
        }

        _services.PeakMeter.Start();

        _mainWindow = new MainWindow(_services);
        MainWindow = _mainWindow;

        try
        {
            SetupTray();
        }
        catch
        {
            // No tray (e.g. restricted shells) must not stop the app from opening.
        }

        try
        {
            SetupHotkey();
        }
        catch
        {
            // A conflicting hotkey registration is non-fatal.
        }

        var startHidden = e.Args.Contains("--tray") || (_services.Controller?.State.Settings.StartMinimized ?? false);
        if (!startHidden)
        {
            _mainWindow.Show();
        }

        MaybeCheckForUpdates();
    }

    private void MaybeCheckForUpdates()
    {
        var settings = _services?.Controller?.State.Settings ?? _services?.StateStore.Load().Settings;
        if (_services is null || settings?.CheckForUpdatesOnStartup != true)
        {
            return;
        }

        // Fire-and-forget, a few seconds after launch so it never delays startup.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4));
                await _services.Update.CheckAsync();
            }
            catch
            {
                // Update checks are best-effort.
            }
        });
    }

    private void SetupTray()
    {
        if (_services is null || _mainWindow is null)
        {
            return;
        }

        var openItem = new System.Windows.Controls.MenuItem { Header = "Open Soundstage" };
        openItem.Click += (_, _) => _mainWindow.ShowFromTray();

        var bypassItem = new System.Windows.Controls.MenuItem { Header = "Bypass all processing", IsCheckable = true };
        bypassItem.Click += (_, _) => _services.Controller?.SetBypass(bypassItem.IsChecked);

        var automationsItem = new System.Windows.Controls.MenuItem { Header = "Automations enabled", IsCheckable = true };
        automationsItem.Click += (_, _) => _services.Controller?.SetAutomationsEnabled(automationsItem.IsChecked);

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => ExitApplication();

        var menu = new System.Windows.Controls.ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(bypassItem);
        menu.Items.Add(automationsItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exitItem);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Soundstage",
            ContextMenu = menu,
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application,
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => _mainWindow.ShowFromTray();

        if (_services.Controller is { } controller)
        {
            controller.Changed += () => UiDispatch.Post(() =>
            {
                bypassItem.IsChecked = controller.State.BypassActive;
                automationsItem.IsChecked = controller.State.AutomationsEnabled;
            });
        }
    }

    private void SetupHotkey()
    {
        if (_services?.Controller is null || _mainWindow is null)
        {
            return;
        }

        _hotkey = new HotkeyService();
        var hotkeyText = _services.Controller.State.Settings.BypassHotkey;
        _hotkey.Register(_mainWindow, hotkeyText, () => _services.Controller?.ToggleBypass());
    }

    private void ExitApplication()
    {
        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Never let a UI glitch take the audio pipeline down silently; keep running.
        MessageBox.Show(
            $"Soundstage hit an unexpected error:\n\n{e.Exception.Message}",
            "Soundstage",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkey?.Dispose();
        _trayIcon?.Dispose();
        _services?.Controller?.SaveState();
        _services?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
