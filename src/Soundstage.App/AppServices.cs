using System.IO;
using Soundstage.Core;
using Soundstage.Core.Abstractions;
using Soundstage.Core.Automation;
using Soundstage.Core.Configio;
using Soundstage.Core.Import;
using Soundstage.Core.Presets;
using Soundstage.Core.State;
using Soundstage.Windows;

namespace Soundstage.App;

/// <summary>
/// Composition root. Deliberately a plain object graph — every dependency is visible here,
/// and viewmodels take what they need from this single instance.
/// </summary>
public sealed class AppServices : IDisposable
{
    public static AppServices? Current { get; private set; }

    public IClock Clock { get; } = SystemClock.Instance;

    public IDelayScheduler Scheduler { get; } = ThreadingDelayScheduler.Instance;

    public PhysicalFileSystem FileSystem { get; } = new();

    public RegistryApoEnvironment RegistryApo { get; } = new();

    public required StateStore StateStore { get; init; }

    public required PresetStore Presets { get; init; }

    public required AutoEqCatalog AutoEq { get; init; }

    public required WasapiAudioEnvironmentSource Environment { get; init; }

    public required PeakMeterService PeakMeter { get; init; }

    public StartupManager Startup { get; } = new();

    /// <summary>Null when Equalizer APO is not installed (UI runs in guidance mode).</summary>
    public ConfigLayout? Layout { get; private set; }

    public BackupService? Backups { get; private set; }

    public TakeoverService? Takeover { get; private set; }

    public ApplyOrchestrator? Orchestrator { get; private set; }

    public SoundstageController? Controller { get; private set; }

    public AutomationCoordinator? Coordinator { get; private set; }

    public bool ApoAvailable => Controller is not null;

    public static AppServices Build()
    {
        var fs = new PhysicalFileSystem();
        var clock = SystemClock.Instance;

        Directory.CreateDirectory(DefaultAppPaths.DataDirectory);
        var stateStore = new StateStore(fs, DefaultAppPaths.StateFilePath);
        var presets = new PresetStore(fs, DefaultAppPaths.PresetDirectory, clock);
        var environment = new WasapiAudioEnvironmentSource(clock);
        environment.Start();

        var services = new AppServices
        {
            StateStore = stateStore,
            Presets = presets,
            AutoEq = AutoEqCatalog.LoadEmbedded(),
            Environment = environment,
            PeakMeter = new PeakMeterService(),
        };

        services.TryInitializeApoPipeline();
        Current = services;
        return services;
    }

    /// <summary>
    /// Wires the APO-dependent pipeline. Config directory resolution order: explicit user
    /// override → registry. Callable again after the user installs APO or fixes the path.
    /// </summary>
    public void TryInitializeApoPipeline()
    {
        if (Controller is not null)
        {
            return;
        }

        var state = StateStore.Load();
        var configDir = state.Settings.ApoConfigDirectoryOverride ?? RegistryApo.ConfigDirectory;
        if (string.IsNullOrWhiteSpace(configDir))
        {
            return;
        }

        Layout = new ConfigLayout(configDir);
        Backups = new BackupService(FileSystem, Layout, Clock);
        Takeover = new TakeoverService(FileSystem, Layout, Backups);
        Orchestrator = new ApplyOrchestrator(FileSystem, Layout, Backups, new RevertGuard(Scheduler));
        Coordinator = new AutomationCoordinator(Environment, Clock, Scheduler);
        Controller = new SoundstageController(StateStore, Presets, Orchestrator, Environment, Coordinator);
    }

    public void Dispose()
    {
        Controller?.Dispose();
        PeakMeter.Dispose();
        Environment.Dispose();
        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
    }
}
