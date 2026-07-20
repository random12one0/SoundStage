using Soundstage.Core.Abstractions;
using Soundstage.Core.Automation;
using Soundstage.Windows;
using Xunit;

namespace Soundstage.App.Tests;

/// <summary>
/// Contract tests for the Windows interop layer. CI runners have no audio endpoints, no
/// Equalizer APO, and no user session niceties — every service must degrade gracefully
/// there, because that's exactly how it must behave on a misconfigured user machine.
/// </summary>
public class WindowsLayerContractTests
{
    [Fact]
    public void WasapiSource_SurvivesAudioLessMachine()
    {
        using var source = new WasapiAudioEnvironmentSource(SystemClock.Instance, TimeSpan.FromMinutes(5));
        source.Start();

        var snapshot = source.GetSnapshot();

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.ActiveAudioProcesses);
        Assert.True(snapshot.Channels >= 1);
        Assert.True(snapshot.SampleRateHz > 0);
    }

    [Fact]
    public void WasapiSource_DoubleStartAndDispose_AreSafe()
    {
        var source = new WasapiAudioEnvironmentSource(SystemClock.Instance);
        source.Start();
        source.Start();
        source.Dispose();
        source.Dispose();
    }

    [Fact]
    public void RegistryApoEnvironment_NeverThrows()
    {
        var env = new RegistryApoEnvironment();
        // CI has no APO; a dev machine might. Either way: consistent, no exceptions.
        Assert.Equal(env.ConfigDirectory is not null, env.IsApoInstalled);
    }

    [Fact]
    public void SpatialProbe_BogusEndpoint_ReturnsUnknownOrOff()
    {
        var state = SpatialAudioProbe.Probe("not-a-real-endpoint-id");
        Assert.True(state is SpatialAudioState.Unknown or SpatialAudioState.Off);
    }

    [Fact]
    public void PeakMeter_StartStop_WithoutAudio_IsSafe()
    {
        using var meter = new PeakMeterService();
        meter.Start();
        meter.Stop();
        Assert.False(meter.IsRunning);
        Assert.True(float.IsNaN(meter.LatestPeak));
    }

    [Fact]
    public void StartupManager_RoundTrips_OnRealRunKey()
    {
        var valueName = $"SoundstageTest-{Guid.NewGuid():N}";
        var manager = new StartupManager(valueName, () => @"C:\Fake\Soundstage.exe");
        try
        {
            Assert.False(manager.IsEnabled);

            manager.SetEnabled(true);
            Assert.True(manager.IsEnabled);

            manager.SetEnabled(false);
            Assert.False(manager.IsEnabled);
        }
        finally
        {
            manager.SetEnabled(false);
        }
    }

    [Fact]
    public void StartupManager_WithEmptyExecutablePath_DoesNotRegister()
    {
        var valueName = $"SoundstageTest-{Guid.NewGuid():N}";
        var manager = new StartupManager(valueName, () => "");
        try
        {
            manager.SetEnabled(true);
            Assert.False(manager.IsEnabled);
        }
        finally
        {
            manager.SetEnabled(false);
        }
    }
}
