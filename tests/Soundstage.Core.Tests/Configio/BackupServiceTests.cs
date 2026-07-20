using Soundstage.Core.Configio;
using Soundstage.Core.Tests.Support;
using Xunit;

namespace Soundstage.Core.Tests.Configio;

public class BackupServiceTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly TestClock _clock = new();
    private readonly ConfigLayout _layout = new(@"C:\config");

    private BackupService CreateService(int chainRetention = 50) => new(_fs, _layout, _clock, chainRetention);

    [Fact]
    public void Backup_WritesTimestampedFile_UnderBackupsDirectory()
    {
        var service = CreateService();
        var path = service.Backup(BackupKind.Chain, "chain content");

        Assert.StartsWith(@"C:\config\Soundstage\backups\chain-", path);
        Assert.EndsWith(".txt", path);
        Assert.Equal("chain content", _fs.ReadAllText(path));
    }

    [Fact]
    public void List_ReturnsNewestFirst_WithKinds()
    {
        var service = CreateService();
        service.Backup(BackupKind.OriginalConfig, "original");
        _clock.Advance(TimeSpan.FromMinutes(1));
        service.Backup(BackupKind.Chain, "chain-1");
        _clock.Advance(TimeSpan.FromMinutes(1));
        service.Backup(BackupKind.Chain, "chain-2");

        var entries = service.List();
        Assert.Equal(3, entries.Count);
        Assert.Equal(BackupKind.Chain, entries[0].Kind);
        Assert.Equal("chain-2", service.Read(entries[0]));
        Assert.Equal(BackupKind.OriginalConfig, entries[2].Kind);
        Assert.True(entries[0].TimestampUtc > entries[1].TimestampUtc);
    }

    [Fact]
    public void Prune_KeepsOnlyRetentionCount_PerKind()
    {
        var service = CreateService(chainRetention: 3);
        for (var i = 0; i < 6; i++)
        {
            service.Backup(BackupKind.Chain, $"chain-{i}");
            _clock.Advance(TimeSpan.FromSeconds(7));
        }

        service.Backup(BackupKind.OriginalConfig, "original");

        var entries = service.List();
        Assert.Equal(3, entries.Count(e => e.Kind == BackupKind.Chain));
        Assert.Single(entries, e => e.Kind == BackupKind.OriginalConfig);

        // The survivors are the newest three.
        var chainContents = entries.Where(e => e.Kind == BackupKind.Chain).Select(service.Read).ToList();
        Assert.Equal(new[] { "chain-5", "chain-4", "chain-3" }, chainContents);
    }

    [Fact]
    public void LatestOriginalConfig_ReturnsNewestOriginal()
    {
        var service = CreateService();
        service.Backup(BackupKind.OriginalConfig, "first");
        _clock.Advance(TimeSpan.FromHours(1));
        service.Backup(BackupKind.Chain, "noise");
        _clock.Advance(TimeSpan.FromHours(1));
        service.Backup(BackupKind.OriginalConfig, "second");

        var latest = service.LatestOriginalConfig();
        Assert.NotNull(latest);
        Assert.Equal("second", service.Read(latest));
    }

    [Fact]
    public void ForeignFilesInBackupDirectory_AreIgnored()
    {
        var service = CreateService();
        _fs.WriteAllTextAtomic(@"C:\config\Soundstage\backups\notes.txt", "user notes");
        _fs.WriteAllTextAtomic(@"C:\config\Soundstage\backups\chain-garbagetimestamp.txt", "junk");
        service.Backup(BackupKind.Chain, "real");

        Assert.Single(service.List());
    }
}
