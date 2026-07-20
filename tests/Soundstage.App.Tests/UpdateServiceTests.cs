using Soundstage.App.Services;
using Soundstage.Core.Update;
using Xunit;

namespace Soundstage.App.Tests;

public class UpdateServiceTests
{
    private sealed class FakeChecker(UpdateInfo? result) : IUpdateChecker
    {
        public Task<UpdateInfo?> GetLatestAsync(CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private static UpdateInfo Release(string tag) =>
        new(UpdatePolicy.TryParseTag(tag, out var v) ? v : new Version(0, 0),
            tag, $"Soundstage {tag}", "notes", "https://example/releases/" + tag,
            [new ReleaseAsset("Soundstage-Setup.exe", "https://example/Setup.exe", 70_000_000, null)]);

    [Fact]
    public async Task NewerRelease_MarksUpdateAvailable()
    {
        var service = new UpdateService(new FakeChecker(Release("v0.9.0")), new Version(0, 3, 0));
        await service.CheckAsync();

        Assert.Equal(UpdateState.Available, service.State);
        Assert.True(service.IsUpdateAvailable);
        Assert.Contains("0.9.0", service.StatusMessage);
    }

    [Fact]
    public async Task SameVersion_ReportsUpToDate()
    {
        var service = new UpdateService(new FakeChecker(Release("v0.3.0")), new Version(0, 3, 0));
        await service.CheckAsync();

        Assert.Equal(UpdateState.UpToDate, service.State);
        Assert.False(service.IsUpdateAvailable);
    }

    [Fact]
    public async Task OlderRelease_IsNotOffered()
    {
        var service = new UpdateService(new FakeChecker(Release("v0.2.0")), new Version(0, 3, 0));
        await service.CheckAsync();
        Assert.Equal(UpdateState.UpToDate, service.State);
    }

    [Fact]
    public async Task Unreachable_FailsGracefully()
    {
        var service = new UpdateService(new FakeChecker(null), new Version(0, 3, 0));
        await service.CheckAsync();
        Assert.Equal(UpdateState.Failed, service.State);
        Assert.False(service.IsUpdateAvailable);
    }
}
