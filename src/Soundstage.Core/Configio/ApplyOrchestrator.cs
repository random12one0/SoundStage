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

    public RevertGuard Guard => _guard;

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

        _fs.WriteAllTextAtomic(_layout.ChainFilePath, newText);

        var shouldGuard = attribution.Source == AttributionSource.Manual
                          && !state.ConfirmedChainHashes.Contains(hash)
                          && currentText is not null;

        if (shouldGuard)
        {
            var revertTo = currentText!;
            _guard.Arm(TimeSpan.FromSeconds(state.Settings.RevertGuardSeconds), () =>
            {
                _fs.WriteAllTextAtomic(_layout.ChainFilePath, revertTo);
            });

            // Wire confirm → remember the hash (idempotent; RevertGuard resolves once).
            void OnResolved(RevertOutcome outcome)
            {
                if (outcome == RevertOutcome.Confirmed)
                {
                    if (!state.ConfirmedChainHashes.Contains(hash))
                    {
                        state.ConfirmedChainHashes.Add(hash);
                        TrimConfirmedHashes(state);
                    }

                    ChainHashConfirmed?.Invoke(hash);
                }

                if (outcome != RevertOutcome.Superseded)
                {
                    _guard.Resolved -= OnResolved;
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

        _fs.WriteAllTextAtomic(_layout.SwitchFilePath, desired);
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

        _fs.WriteAllTextAtomic(_layout.ChainFilePath, content);
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
                _fs.WriteAllTextAtomic(_layout.ChainFilePath, original);
            }
        }

        _fs.WriteAllTextAtomic(_layout.ChainFilePath, chainText);
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
            _fs.WriteAllTextAtomic(_layout.SwitchFilePath, desired);
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
