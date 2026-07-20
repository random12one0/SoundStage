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
    private bool _confirmNewSounds;

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

    // ---- Updates ----

    [ObservableProperty]
    private bool _checkForUpdatesOnStartup = true;

    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    private bool _updateAvailable;

    [ObservableProperty]
    private bool _isChecking;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private double _downloadProgress;

    public string CurrentVersionText => $"Current version: {Core.Update.UpdatePolicy.Format(_services.Update.CurrentVersion)}";

    public string AboutText =>
        $"Soundstage {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)} — a front-end and automation layer for Equalizer APO.\n" +
        "Bundled data: AutoEq headphone corrections (MIT, github.com/jaakkopasanen/AutoEq).\n" +
        "Built with WPF-UI (MIT), NAudio (MIT), CommunityToolkit.Mvvm (MIT), Hardcodet NotifyIcon (CPOL).";

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        services.Update.Changed += () => UiDispatch.Post(SyncUpdateState);
        Sync();
        SyncUpdateState();
        RefreshBackups();
    }

    private void Sync()
    {
        _syncing = true;
        LaunchOnBoot = _services.Startup.IsEnabled;
        var settings = _services.Controller?.State.Settings ?? _services.StateStore.Load().Settings;
        StartMinimized = settings.StartMinimized;
        MinimizeToTray = settings.MinimizeToTray;
        AmbienceFeature = settings.AmbienceFeatureEnabled;
        ConfirmNewSounds = settings.ConfirmNewSounds;
        GuardSeconds = settings.RevertGuardSeconds;
        SafetyMargin = settings.SafetyMarginDb;
        BypassHotkeyText = settings.BypassHotkey;
        CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        _syncing = false;
    }

    private void SyncUpdateState()
    {
        var update = _services.Update;
        UpdateStatus = update.StatusMessage;
        UpdateAvailable = update.IsUpdateAvailable;
        IsChecking = update.State == Services.UpdateState.Checking;
        IsDownloading = update.State is Services.UpdateState.Downloading or Services.UpdateState.ReadyToInstall;
        DownloadProgress = update.DownloadProgress * 100.0;
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
        settings.ConfirmNewSounds = ConfirmNewSounds;
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

    partial void OnConfirmNewSoundsChanged(bool value) => Persist();

    partial void OnGuardSecondsChanged(double value) => Persist();

    partial void OnCheckForUpdatesOnStartupChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        // Persist even when APO isn't set up (Controller may be null).
        if (_services.Controller is { } controller)
        {
            controller.State.Settings.CheckForUpdatesOnStartup = value;
            controller.SaveState();
        }
        else
        {
            var state = _services.StateStore.Load();
            state.Settings.CheckForUpdatesOnStartup = value;
            _services.StateStore.Save(state);
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync() => await _services.Update.CheckAsync();

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        await _services.Update.DownloadAndInstallAsync(() => UiDispatch.Post(() =>
            System.Windows.Application.Current?.Shutdown()));
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = _services.Update.Latest?.HtmlUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Non-critical.
        }
    }

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
