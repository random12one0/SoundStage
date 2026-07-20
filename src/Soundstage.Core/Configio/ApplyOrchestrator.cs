using System.Security.Cryptography;
using System.Text;
using Soundstage.Core.Abstractions;
using Soundstage.Core.Presets;
using Soundstage.Core.State;

namespace Soundstage.Core.Configio;

public sealed record ApplyResult(
    bool Changed,
    bool Guarded,
    string ChainHash,
    ChainCompilation Compilation);

/// <summary>
/// A config write couldn't complete (permission denied, or a lock that outlasted the retry
/// window). Reported instead of thrown, so a failed background revert or a foreground toggle
/// never crashes the app with a raw "Access to the path is denied" dialog.
/// </summary>
public sealed record ConfigWriteBlocked(string Path, Exception Error)
{
    public string FriendlyMessage =>
        "Windows blocked writing to the Equalizer APO config folder, so your last change may not have taken effect. "
        + "Fix it once by running Soundstage as administrator, or grant your Windows account write access to the "
        + "EqualizerAPO\\config folder.";
}

/// <summary>
/// The single entry point for changing what Equalizer APO does:
/// compile → clamp → backup → atomic write → (maybe) arm the revert guard.
///
/// Guard policy — reconciling "instant preset switching, no confirm step" with
/// "auto-revert bad configs":
/// <list type="bullet">
/// <item>Manual applies of a chain whose hash was previously confirmed → instant, no guard.</item>
/// <item>Manual applies of a never-confirmed chain → guarded (countdown toast; ignoring it reverts).</item>
/// <item>Automation/system applies → never guarded (nobody is there to confirm; reverting would
/// fight the rule engine — backups still capture every step).</item>
/// <item>Bypass toggles → never guarded; they only rewrite the tiny switch file.</item>
/// </list>
/// </summary>
public sealed class ApplyOrchestrator
{
    private readonly IFileSystem _fs;
    private readonly ConfigLayout _layout;
    private readonly BackupService _backups;
    private readonly RevertGuard _guard;

    public ApplyOrchestrator(IFileSystem fs, ConfigLayout layout, BackupService backups, RevertGuard guard)
    {
        _fs = fs;
        _layout = layout;
        _backups = backups;
        _guard = guard;
    }

    /// <summary>Raised when a guarded apply's hash gets confirmed (persist the state!).</summary>
    public event Action<string>? ChainHashConfirmed;

    /// <summary>Raised when a config write was blocked instead of throwing (see <see cref="ConfigWriteBlocked"/>).</summary>
    public event Action<ConfigWriteBlocked>? WriteBlocked;

    public RevertGuard Guard => _guard;

    /// <summary>
    /// Writes a config file, converting a hard permission/lock failure into a
    /// <see cref="WriteBlocked"/> signal rather than an exception. Every config write in this
    /// class goes through here so nothing — foreground toggle or background revert timer — can
    /// crash the app on "Access to the path is denied".
    /// </summary>
    private bool TryWrite(string path, string content)
    {
        try
        {
            _fs.WriteAllTextAtomic(path, content);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            WriteBlocked?.Invoke(new ConfigWriteBlocked(path, ex));
            return false;
        }
    }

    public ApplyResult Apply(
        SoundstageState state,
        Func<string, EqPreset?> presetResolver,
        ApplyAttribution attribution,
        Func<int, string?>? ambienceIrResolver = null)
    {
        var compilation = ChainCompiler.Compile(state, presetResolver, ambienceIrResolver);
        var newText = compilation.RenderedText;
        var hash = ComputeHash(newText);

        EnsureSwitchMatchesBypassState(state);

        var currentText = _fs.FileExists(_layout.ChainFilePath) ? _fs.ReadAllText(_layout.ChainFilePath) : null;
        state.LastAttribution = attribution;

        if (currentText == newText)
        {
            return new ApplyResult(Changed: false, Guarded: false, hash, compilation);
        }

        if (currentText is not null)
        {
            _backups.Backup(BackupKind.Chain, currentText);
        }

        TryWrite(_layout.ChainFilePath, newText);

        var shouldGuard = state.Settings.ConfirmNewSounds
                          && attribution.Source == AttributionSource.Manual
                          && !state.ConfirmedChainHashes.Contains(hash)
                          && currentText is not null;

        if (shouldGuard)
        {
            var revertTo = currentText!;
            _guard.Arm(TimeSpan.FromSeconds(state.Settings.RevertGuardSeconds), () =>
            {
                TryWrite(_layout.ChainFilePath, revertTo);
            });

            // Wire confirm → remember the hash. The handler detaches on the FIRST outcome
            // it sees: a Superseded outcome means a newer apply took over the pending
            // guard, and confirming THAT one must not whitelist this apply's hash — the
            // user never chose to keep this sound. (Arm fires Superseded before the newer
            // apply subscribes its own handler, so the newer handler never sees it.)
            void OnResolved(RevertOutcome outcome)
            {
                _guard.Resolved -= OnResolved;
                if (outcome == RevertOutcome.Confirmed)
                {
                    if (!state.ConfirmedChainHashes.Contains(hash))
                    {
                        state.ConfirmedChainHashes.Add(hash);
                        TrimConfirmedHashes(state);
                    }

                    ChainHashConfirmed?.Invoke(hash);
                }
            }

            _guard.Resolved += OnResolved;
        }

        return new ApplyResult(Changed: true, Guarded: shouldGuard, hash, compilation);
    }

    /// <summary>
    /// A/B bypass: rewrites only the tiny switch file. Instant, never guarded, and the
    /// chain file is untouched so toggling back is equally instant.
    /// </summary>
    public bool SetBypass(SoundstageState state, bool bypassed)
    {
        state.BypassActive = bypassed;
        var desired = _layout.BuildSwitchContent(bypassed);
        var current = _fs.FileExists(_layout.SwitchFilePath) ? _fs.ReadAllText(_layout.SwitchFilePath) : null;
        if (current == desired)
        {
            return false;
        }

        TryWrite(_layout.SwitchFilePath, desired);
        return true;
    }

    /// <summary>Restores a backup as the active chain (user-driven, from the restore list).</summary>
    public void RestoreBackup(BackupEntry entry)
    {
        var content = _backups.Read(entry);
        var current = _fs.FileExists(_layout.ChainFilePath) ? _fs.ReadAllText(_layout.ChainFilePath) : null;
        if (current is not null && current != content)
        {
            _backups.Backup(BackupKind.Chain, current);
        }

        TryWrite(_layout.ChainFilePath, content);
    }

    /// <summary>
    /// Applies <paramref name="chainText"/> for <paramref name="duration"/>, then restores
    /// what was there — used by the Atmos audibility diagnostic. Disposing the handle
    /// restores early. Never guarded, never backed up (the original text is held in memory
    /// and also still present in the regular backup history).
    /// </summary>
    public IDisposable ApplyTemporary(string chainText, TimeSpan duration, Abstractions.IDelayScheduler scheduler)
    {
        var original = _fs.FileExists(_layout.ChainFilePath) ? _fs.ReadAllText(_layout.ChainFilePath) : "";
        var restored = 0;
        void Restore()
        {
            if (Interlocked.Exchange(ref restored, 1) == 0)
            {
                TryWrite(_layout.ChainFilePath, original);
            }
        }

        TryWrite(_layout.ChainFilePath, chainText);
        var handle = scheduler.Schedule(duration, Restore);
        return new TemporaryApplyHandle(handle, Restore);
    }

    private sealed class TemporaryApplyHandle(IDisposable timer, Action restore) : IDisposable
    {
        public void Dispose()
        {
            timer.Dispose();
            restore();
        }
    }

    public static string ComputeHash(string chainText)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(chainText));
        return Convert.ToHexString(bytes);
    }

    private void EnsureSwitchMatchesBypassState(SoundstageState state)
    {
        var desired = _layout.BuildSwitchContent(state.BypassActive);
        var current = _fs.FileExists(_layout.SwitchFilePath) ? _fs.ReadAllText(_layout.SwitchFilePath) : null;
        if (current != desired)
        {
            TryWrite(_layout.SwitchFilePath, desired);
        }
    }

    private static void TrimConfirmedHashes(SoundstageState state)
    {
        const int maxRemembered = 200;
        while (state.ConfirmedChainHashes.Count > maxRemembered)
        {
            state.ConfirmedChainHashes.RemoveAt(0);
        }
    }
}
