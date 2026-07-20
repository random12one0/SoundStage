using Soundstage.Core.Effects;
using Soundstage.Core.State;
using Soundstage.Core.Tests.Support;
using Xunit;

namespace Soundstage.Core.Tests.State;

public class StateStoreTests
{
    private readonly InMemoryFileSystem _fs = new();
    private const string Path = @"C:\AppData\Soundstage\state.json";

    [Fact]
    public void MissingFile_LoadsDefaults()
    {
        var state = new StateStore(_fs, Path).Load();
        Assert.True(state.AutomationsEnabled);
        Assert.False(state.BypassActive);
        Assert.Empty(state.Profiles);
        Assert.Equal(10, state.Settings.RevertGuardSeconds);
    }

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var store = new StateStore(_fs, Path);
        var state = new SoundstageState { BypassActive = true, AutomationsEnabled = false };
        var profile = state.GetOrCreateProfile("{0.0.0.00000000}.{abc-def}", "Receiver");
        profile.ActivePresetId = "music";
        profile.LastKnownChannels = 6;
        profile.Effects = profile.Effects with
        {
            NightMode = new NightModeSettings(Enabled: true, Intensity: 65),
            StereoWidth = new StereoWidthSettings(Enabled: true, WidthPercent: 115),
        };
        state.ConfirmedChainHashes.Add("HASH1");
        state.LastAttribution = ApplyAttribution.Rule("r1", "Night rule");

        store.Save(state);
        var loaded = store.Load();

        Assert.True(loaded.BypassActive);
        Assert.False(loaded.AutomationsEnabled);
        var p = Assert.Single(loaded.Profiles);
        Assert.Equal("Receiver", p.FriendlyName);
        Assert.Equal(6, p.LastKnownChannels);
        Assert.True(p.Effects.NightMode.Enabled);
        Assert.Equal(65, p.Effects.NightMode.Intensity);
        Assert.Equal(115, p.Effects.StereoWidth.WidthPercent);
        Assert.Contains("HASH1", loaded.ConfirmedChainHashes);
        Assert.Equal("Night rule", loaded.LastAttribution!.RuleName);
        Assert.Equal(AttributionSource.Rule, loaded.LastAttribution.Source);
    }

    [Fact]
    public void CorruptFile_IsQuarantined_AndDefaultsLoad()
    {
        _fs.WriteAllTextAtomic(Path, "{broken json!!");
        var state = new StateStore(_fs, Path).Load();

        Assert.Empty(state.Profiles);
        Assert.True(_fs.FileExists(Path + ".corrupt"));
        Assert.True(_fs.FileExists(Path)); // original untouched until next save
    }

    [Fact]
    public void GetOrCreateProfile_IsIdempotent_AndUpdatesFriendlyName()
    {
        var state = new SoundstageState();
        var a = state.GetOrCreateProfile("id1", "Old Name");
        var b = state.GetOrCreateProfile("id1", "New Name");

        Assert.Same(a, b);
        Assert.Single(state.Profiles);
        Assert.Equal("New Name", a.FriendlyName);
    }
}
