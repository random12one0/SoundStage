using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Soundstage.App.Services;
using Soundstage.Core.Configio;

namespace Soundstage.App.ViewModels;

public partial class BackupEntryViewModel(BackupEntry entry) : ObservableObject
{
    public BackupEntry Entry { get; } = entry;

    public string Description =>
        $"{(Entry.Kind == BackupKind.OriginalConfig ? "Original config" : "Chain")} · {Entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _syncing;

    [ObservableProperty]
    private bool _launchOnBoot;

    [ObservableProperty]
    private bool _startMinimized;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _ambienceFeature;

    [ObservableProperty]
    private double _guardSeconds = 10;

    [ObservableProperty]
    private double _safetyMargin = 0.5;

    [ObservableProperty]
    private string _bypassHotkeyText = "Ctrl+Alt+B";

    [ObservableProperty]
    private ObservableCollection<BackupEntryViewModel> _backups = [];

    [ObservableProperty]
    private string _statusMessage = "";

    public string AboutText =>
        $"Soundstage {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)} — a front-end and automation layer for Equalizer APO.\n" +
        "Bundled data: AutoEq headphone corrections (MIT, github.com/jaakkopasanen/AutoEq).\n" +
        "Built with WPF-UI (MIT), NAudio (MIT), CommunityToolkit.Mvvm (MIT), Hardcodet NotifyIcon (CPOL).";

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        Sync();
        RefreshBackups();
    }

    private void Sync()
    {
        _syncing = true;
        LaunchOnBoot = _services.Startup.IsEnabled;
        var settings = _services.Controller?.State.Settings;
        if (settings is not null)
        {
            StartMinimized = settings.StartMinimized;
            MinimizeToTray = settings.MinimizeToTray;
            AmbienceFeature = settings.AmbienceFeatureEnabled;
            GuardSeconds = settings.RevertGuardSeconds;
            SafetyMargin = settings.SafetyMarginDb;
            BypassHotkeyText = settings.BypassHotkey;
        }

        _syncing = false;
    }

    private void Persist(bool reapply = false)
    {
        if (_syncing)
        {
            return;
        }

        var controller = _services.Controller;
        if (controller is null)
        {
            return;
        }

        var settings = controller.State.Settings;
        settings.StartMinimized = StartMinimized;
        settings.MinimizeToTray = MinimizeToTray;
        settings.AmbienceFeatureEnabled = AmbienceFeature;
        settings.RevertGuardSeconds = (int)Math.Clamp(GuardSeconds, 3, 60);
        settings.SafetyMarginDb = Math.Clamp(SafetyMargin, 0, 3);
        settings.BypassHotkey = BypassHotkeyText;
        controller.SaveState();
        if (reapply)
        {
            controller.Apply(Core.State.ApplyAttribution.System("settings changed"));
        }
    }

    partial void OnLaunchOnBootChanged(bool value)
    {
        if (!_syncing)
        {
            try
            {
                _services.Startup.SetEnabled(value);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not update startup registration: {ex.Message}";
            }
        }
    }

    partial void OnStartMinimizedChanged(bool value) => Persist();

    partial void OnMinimizeToTrayChanged(bool value) => Persist();

    partial void OnAmbienceFeatureChanged(bool value) => Persist(reapply: true);

    partial void OnGuardSecondsChanged(double value) => Persist();

    partial void OnSafetyMarginChanged(double value) => Persist(reapply: true);

    [RelayCommand]
    private void RefreshBackups()
    {
        var entries = _services.Backups?.List() ?? [];
        Backups = new ObservableCollection<BackupEntryViewModel>(entries.Select(e => new BackupEntryViewModel(e)));
    }

    [RelayCommand]
    private void RestoreBackup(BackupEntryViewModel? row)
    {
        if (row is null || _services.Orchestrator is null)
        {
            return;
        }

        try
        {
            if (row.Entry.Kind == BackupKind.OriginalConfig)
            {
                _services.FileSystem.WriteAllTextAtomic(_services.Layout!.ConfigTxtPath, _services.Backups!.Read(row.Entry));
                StatusMessage = "Restored the original config.txt (Soundstage is now out of the signal path — use Diagnostics to re-take control).";
            }
            else
            {
                _services.Orchestrator.RestoreBackup(row.Entry);
                StatusMessage = $"Restored {row.Description}.";
            }

            RefreshBackups();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        var dir = _services.Layout?.ConfigDirectory;
        if (dir is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
            catch
            {
                // Non-critical.
            }
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", Core.Abstractions.DefaultAppPaths.DataDirectory) { UseShellExecute = true });
        }
        catch
        {
            // Non-critical.
        }
    }
}
