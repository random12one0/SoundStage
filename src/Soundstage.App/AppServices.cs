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

    public Services.VstPluginService Vst { get; } = new();

    public Services.UpdateService Update { get; private set; } = null!;

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

        var appVersion = typeof(AppServices).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var persisted = stateStore.Load();
        services.Update = new Services.UpdateService(persisted.Settings.UpdateOwner, persisted.Settings.UpdateRepo, appVersion);

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
        Controller = new SoundstageController(StateStore, Presets, Orchestrator, Environment, Coordinator, Scheduler)
        {
            AmbienceIrResolver = ResolveAmbienceIr,
            VstPluginResolver = Vst.Resolve,
        };
    }

    /// <summary>Writes (once) and returns the chain-relative path of the ambience IR for a sample rate.</summary>
    private string? ResolveAmbienceIr(int sampleRate)
    {
        var layout = Layout;
        var controller = Controller;
        if (layout is null)
        {
            return null;
        }

        try
        {
            var intensity = controller?.ActiveProfile?.Effects.Ambience.Intensity ?? 30;
            var fileName = Core.Effects.IrGenerator.FileNameFor(sampleRate, intensity);
            var fullPath = Path.Combine(layout.IrDirectory, fileName);
            Directory.CreateDirectory(layout.IrDirectory);
            if (!File.Exists(fullPath))
            {
                File.WriteAllBytes(fullPath, Core.Effects.IrGenerator.BuildWav(sampleRate, intensity));
                CleanStaleIrFiles(layout.IrDirectory);
            }

            // Return the ABSOLUTE path. Equalizer APO's resolution of relative Convolution paths
            // (relative to the config root vs. the included file's folder) is ambiguous enough that
            // a wrong guess means the reverb silently never loads — an absolute path removes all doubt.
            return fullPath;
        }
        catch
        {
            return null; // ambience silently skips; the compiler notes it
        }
    }

    /// <summary>Deletes impulse responses from older IR-generator versions so the cache folder
    /// doesn't accumulate stale files after an ambience rework. Best-effort — failures are ignored.</summary>
    private static void CleanStaleIrFiles(string irDirectory)
    {
        var current = $"ambience-v{Core.Effects.IrGenerator.Version}-";
        foreach (var path in Directory.EnumerateFiles(irDirectory, "ambience-*.wav"))
        {
            if (!Path.GetFileName(path).StartsWith(current, StringComparison.Ordinal))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Leftover file is harmless; ignore.
                }
            }
        }
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
