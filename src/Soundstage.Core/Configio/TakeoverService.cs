using Soundstage.Core.Abstractions;

namespace Soundstage.Core.Configio;

/// <summary>What was found in config.txt before/without Soundstage ownership.</summary>
public enum ExistingConfigKind
{
    Missing,
    StockDefault,
    Peace,
    Custom,
}

public enum OwnershipState
{
    /// <summary>config.txt is our pristine stub; the include chain is intact.</summary>
    Owned,

    /// <summary>config.txt contains our marker but is not the expected stub (edited by hand or a tool).</summary>
    OwnedButModified,

    /// <summary>config.txt exists and belongs to someone else (Peace, hand-written, or the stock install).</summary>
    NotOwned,

    /// <summary>config.txt does not exist.</summary>
    MissingConfig,
}

public sealed record TakeoverReport(bool TookOwnership, string? OriginalBackupPath, ExistingConfigKind PreviousKind);

/// <summary>
/// Establishes and verifies Soundstage's ownership of Equalizer APO's config.txt.
/// The prime directive: never destroy anything a user wrote — everything foreign is
/// backed up before the stub goes in, and disowning restores the newest original.
/// </summary>
public sealed class TakeoverService
{
    private readonly IFileSystem _fs;
    private readonly ConfigLayout _layout;
    private readonly BackupService _backups;

    public TakeoverService(IFileSystem fs, ConfigLayout layout, BackupService backups)
    {
        _fs = fs;
        _layout = layout;
        _backups = backups;
    }

    public static ExistingConfigKind Classify(string? configContent)
    {
        if (configContent is null)
        {
            return ExistingConfigKind.Missing;
        }

        if (configContent.Contains("peace", StringComparison.OrdinalIgnoreCase))
        {
            return ExistingConfigKind.Peace;
        }

        // The config.txt an Equalizer APO install ships with.
        var normalized = configContent.Replace("\r\n", "\n");
        if (normalized.Contains("Preamp: -6 dB", StringComparison.OrdinalIgnoreCase)
            && normalized.Contains("Include: example.txt", StringComparison.OrdinalIgnoreCase))
        {
            return ExistingConfigKind.StockDefault;
        }

        return ExistingConfigKind.Custom;
    }

    public OwnershipState GetOwnershipState()
    {
        if (!_fs.FileExists(_layout.ConfigTxtPath))
        {
            return OwnershipState.MissingConfig;
        }

        var content = _fs.ReadAllText(_layout.ConfigTxtPath);
        if (!ConfigLayout.ContainsManagedMarker(content))
        {
            return OwnershipState.NotOwned;
        }

        return content == _layout.BuildStubContent() ? OwnershipState.Owned : OwnershipState.OwnedButModified;
    }

    /// <summary>
    /// Takes (or re-takes) ownership. Any foreign config.txt content is backed up first.
    /// Idempotent: an already-owned setup produces no writes and no new backups.
    /// </summary>
    public TakeoverReport TakeOwnership()
    {
        var state = GetOwnershipState();
        string? backupPath = null;
        var previousKind = ExistingConfigKind.Missing;

        if (state == OwnershipState.Owned)
        {
            EnsureManagedFiles();
            return new TakeoverReport(TookOwnership: false, OriginalBackupPath: null, PreviousKind: ExistingConfigKind.Missing);
        }

        if (state is OwnershipState.NotOwned or OwnershipState.OwnedButModified)
        {
            var existing = _fs.ReadAllText(_layout.ConfigTxtPath);
            previousKind = state == OwnershipState.NotOwned ? Classify(existing) : ExistingConfigKind.Custom;
            backupPath = _backups.Backup(BackupKind.OriginalConfig, existing);
        }

        _fs.CreateDirectory(_layout.AppDirectory);
        EnsureManagedFiles();
        _fs.WriteAllTextAtomic(_layout.ConfigTxtPath, _layout.BuildStubContent());

        return new TakeoverReport(TookOwnership: true, OriginalBackupPath: backupPath, PreviousKind: previousKind);
    }

    /// <summary>
    /// Returns config.txt to the user: restores the most recent pre-takeover backup, or the
    /// stock default when none exists. Soundstage's own directory (including backups) is left intact.
    /// </summary>
    public void Disown()
    {
        var original = _backups.LatestOriginalConfig();
        var content = original is not null
            ? _backups.Read(original)
            : "# Default config file for Equalizer APO\r\nPreamp: -6 dB\r\nInclude: example.txt\r\n";
        _fs.WriteAllTextAtomic(_layout.ConfigTxtPath, content);
    }

    /// <summary>Creates the managed files that must exist for the include chain to resolve.</summary>
    private void EnsureManagedFiles()
    {
        _fs.CreateDirectory(_layout.AppDirectory);
        _fs.CreateDirectory(_layout.BackupsDirectory);

        if (!_fs.FileExists(_layout.UserFilePath))
        {
            _fs.WriteAllTextAtomic(_layout.UserFilePath, _layout.BuildInitialUserContent());
        }

        if (!_fs.FileExists(_layout.ChainFilePath))
        {
            _fs.WriteAllTextAtomic(_layout.ChainFilePath, _layout.BuildEmptyChainContent());
        }

        if (!_fs.FileExists(_layout.SwitchFilePath))
        {
            _fs.WriteAllTextAtomic(_layout.SwitchFilePath, _layout.BuildSwitchContent(bypassed: false));
        }
    }
}
