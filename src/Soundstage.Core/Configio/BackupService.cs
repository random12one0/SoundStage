using System.Globalization;
using Soundstage.Core.Abstractions;

namespace Soundstage.Core.Configio;

public enum BackupKind
{
    /// <summary>The generated chain.txt before it was replaced.</summary>
    Chain,

    /// <summary>A config.txt that existed before Soundstage took ownership (Peace's, hand-written, or stock).</summary>
    OriginalConfig,
}

public sealed record BackupEntry(BackupKind Kind, DateTimeOffset TimestampUtc, string Path, long SizeBytes);

/// <summary>
/// Timestamped copies of every config file Soundstage replaces, with per-kind retention.
/// Original (pre-takeover) configs are retained more aggressively than routine chain backups —
/// they are the user's own work.
/// </summary>
public sealed class BackupService
{
    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";

    private readonly IFileSystem _fs;
    private readonly ConfigLayout _layout;
    private readonly IClock _clock;

    public BackupService(IFileSystem fs, ConfigLayout layout, IClock clock, int chainRetention = 50, int originalRetention = 20)
    {
        _fs = fs;
        _layout = layout;
        _clock = clock;
        ChainRetention = chainRetention;
        OriginalRetention = originalRetention;
    }

    public int ChainRetention { get; }

    public int OriginalRetention { get; }

    /// <summary>Stores <paramref name="content"/> as a new backup and prunes old ones. Returns the backup path.</summary>
    public string Backup(BackupKind kind, string content)
    {
        _fs.CreateDirectory(_layout.BackupsDirectory);
        var timestamp = _clock.UtcNow.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var path = $"{_layout.BackupsDirectory}\\{Prefix(kind)}-{timestamp}.txt";
        _fs.WriteAllTextAtomic(path, content);
        Prune(kind);
        return path;
    }

    public IReadOnlyList<BackupEntry> List()
    {
        var entries = new List<BackupEntry>();
        foreach (var path in _fs.ListFiles(_layout.BackupsDirectory, "*.txt"))
        {
            if (TryParseEntry(path, out var entry))
            {
                entries.Add(entry);
            }
        }

        return entries.OrderByDescending(e => e.TimestampUtc).ToList();
    }

    public string Read(BackupEntry entry) => _fs.ReadAllText(entry.Path);

    /// <summary>Most recent original-config backup, if any (used by disown/restore).</summary>
    public BackupEntry? LatestOriginalConfig() => List().FirstOrDefault(e => e.Kind == BackupKind.OriginalConfig);

    private void Prune(BackupKind kind)
    {
        var keep = kind == BackupKind.Chain ? ChainRetention : OriginalRetention;
        var entries = List().Where(e => e.Kind == kind).ToList();
        foreach (var stale in entries.Skip(keep))
        {
            _fs.DeleteFile(stale.Path);
        }
    }

    private static string Prefix(BackupKind kind) => kind switch
    {
        BackupKind.Chain => "chain",
        BackupKind.OriginalConfig => "original-config",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private bool TryParseEntry(string path, out BackupEntry entry)
    {
        entry = null!;
        var fileName = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        if (!fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^4];
        BackupKind kind;
        string timestampPart;
        if (stem.StartsWith("original-config-", StringComparison.Ordinal))
        {
            kind = BackupKind.OriginalConfig;
            timestampPart = stem["original-config-".Length..];
        }
        else if (stem.StartsWith("chain-", StringComparison.Ordinal))
        {
            kind = BackupKind.Chain;
            timestampPart = stem["chain-".Length..];
        }
        else
        {
            return false;
        }

        if (!DateTime.TryParseExact(timestampPart, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            return false;
        }

        long size;
        try
        {
            size = _fs.ReadAllText(path).Length;
        }
        catch
        {
            size = 0;
        }

        entry = new BackupEntry(kind, new DateTimeOffset(timestamp, TimeSpan.Zero), path, size);
        return true;
    }
}
