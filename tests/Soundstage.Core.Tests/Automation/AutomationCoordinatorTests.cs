using Soundstage.Core.Automation;
using Soundstage.Core.Effects;
using Soundstage.Core.State;
using Soundstage.Core.Tests.Support;
using Xunit;

namespace Soundstage.Core.Tests.Automation;

public class AutomationCoordinatorTests
{
    private sealed class FakeEnvironmentSource : IAudioEnvironmentSource
    {
        public AudioEnvironmentSnapshot Snapshot { get; set; } =
            AudioEnvironmentSnapshot.Empty(DateTimeOffset.Parse("2026-07-20T14:00:00+02:00"));

        public event Action? Changed;

        public AudioEnvironmentSnapshot GetSnapshot() => Snapshot;

        public void Raise() => Changed?.Invoke();
    }

    private readonly FakeEnvironmentSource _source = new();
    private readonly TestClock _clock = new();
    private readonly TestScheduler _scheduler = new();
    private readonly SoundstageState _state = new();
    private readonly DeviceProfile _profile;
    private readonly List<string> _log = [];
    private readonly AutomationCoordinator _coordinator;

    public AutomationCoordinatorTests()
    {
        _profile = _state.GetOrCreateProfile("{dev-1}", "Speakers");
        _profile.Rules.Clear();
        _state.ActiveEndpointId = "{dev-1}";

        _coordinator = new AutomationCoordinator(_source, _clock, _scheduler)
        {
            StateProvider = () => _state,
            SwitchPreset = (id, attr) =>
            {
                _profile.ActivePresetId = id;
                _log.Add($"preset={id} by {attr.RuleName}");
            },
            SetEffectEnabled = (kind, on, attr) =>
            {
                _profile.Effects = kind switch
                {
                    EffectKind.NightMode => _profile.Effects with { NightMode = _profile.Effects.NightMode with { Enabled = on } },
                    EffectKind.Loudness => _profile.Effects with { Loudness = _profile.Effects.Loudness with { Enabled = on } },
                    EffectKind.StereoWidth => _profile.Effects with { StereoWidth = _profile.Effects.StereoWidth with { Enabled = on } },
                    _ => _profile.Effects,
                };
                _log.Add($"{kind}={on} by {attr.RuleName}");
            },
            SetEffectIntensity = (kind, value, attr) => _log.Add($"{kind}:{value} by {attr.RuleName}"),
        };
    }

    private void SetEnvironment(string time, int channels = 2, params string[] apps)
    {
        _source.Snapshot = new AudioEnvironmentSnapshot(
            DateTimeOffset.Parse(time), "{dev-1}", "Speakers", channels, 48000, 24, SpatialAudioState.Unknown, apps);
    }

    [Fact]
    public void MatchingRule_AppliesActions_WithRuleAttribution()
    {
        _profile.Rules.Add(new AutomationRule
        {
            Id = "spotify",
            Name = "Music when Spotify plays",
            Trigger = new AudioAppTrigger(["spotify"]),
            Actions = [new SwitchPresetAction("music")],
        });
        SetEnvironment("2026-07-20T14:00:00+02:00", 2, "Spotify");

        _coordinator.Reevaluate();

        Assert.Equal("music", _profile.ActivePresetId);
        Assert.Equal(new[] { "preset=music by Music when Spotify plays" }, _log);
        Assert.NotNull(_coordinator.LastDecision);
        Assert.Single(_coordinator.LastDecision.AppliedChanges);
    }

    [Fact]
    public void AlreadySatisfiedActions_AreNotReapplied()
    {
        _profile.Rules.Add(new AutomationRule
        {
            Id = "r",
            Name = "r",
            Trigger = new AudioAppTrigger(["spotify"]),
            Actions = [new SwitchPresetAction("music")],
        });
        SetEnvironment("2026-07-20T14:00:00+02:00", 2, "Spotify");

        _coordinator.Reevaluate();
        _coordinator.Reevaluate();

        Assert.Single(_log); // second evaluation found nothing to change
        Assert.Empty(_coordinator.LastDecision!.AppliedChanges);
    }

    [Fact]
    public void MasterKillSwitch_StopsAllActions_ButKeepsRules()
    {
        _profile.Rules.Add(new AutomationRule
        {
            Id = "r",
            Name = "r",
            Trigger = new AudioAppTrigger(["spotify"]),
            Actions = [new SwitchPresetAction("music")],
        });
        _state.AutomationsEnabled = false;
        SetEnvironment("2026-07-20T14:00:00+02:00", 2, "Spotify");

        _coordinator.Reevaluate();

        Assert.Null(_profile.ActivePresetId);
        Assert.Empty(_log);
        Assert.Single(_profile.Rules);
        Assert.Contains("master switch", _coordinator.LastDecision!.AppliedChanges[0]);
    }

    [Fact]
    public void EnvironmentChanges_AreDebounced()
    {
        _profile.Rules.Add(new AutomationRule
        {
            Id = "r",
            Name = "r",
            Trigger = new AudioAppTrigger(["spotify"]),
            Actions = [new SwitchPresetAction("music")],
        });
        SetEnvironment("2026-07-20T14:00:00+02:00", 2, "Spotify");

        _source.Raise();
        _source.Raise();
        _source.Raise();
        Assert.Empty(_log); // nothing until the debounce fires

        _scheduler.FireAll();

        Assert.Single(_log);
    }

    [Fact]
    public void DaySimulation_EveningPlaythrough()
    {
        // Rules: flat on multichannel; music when Spotify; night mode 22:00–06:00.
        _profile.Rules.AddRange(
        [
            new AutomationRule
            {
                Id = "multichannel", Name = "Flat for movies",
                Trigger = new ChannelCountTrigger(ChannelCondition.Multichannel),
                Actions = [new SwitchPresetAction("flat")],
            },
            new AutomationRule
            {
                Id = "spotify", Name = "Music for Spotify",
                Trigger = new AudioAppTrigger(["spotify"]),
                Actions = [new SwitchPresetAction("music")],
            },
            new AutomationRule
            {
                Id = "night", Name = "Night mode after 10pm",
                Trigger = new ScheduleTrigger(ScheduleTrigger.EveryDay, new TimeOnly(22, 0), new TimeOnly(6, 0)),
                Actions = [new SetEffectEnabledAction(EffectKind.NightMode, true)],
            },
        ]);

        // 20:00 — Spotify, stereo.
        SetEnvironment("2026-07-20T20:00:00+02:00", 2, "Spotify");
        _coordinator.Reevaluate();
        Assert.Equal("music", _profile.ActivePresetId);
        Assert.False(_profile.Effects.NightMode.Enabled);

        // 21:00 — movie starts, receiver switches to 5.1. Spotify paused.
        SetEnvironment("2026-07-20T21:00:00+02:00", 6);
        _coordinator.Reevaluate();
        Assert.Equal("flat", _profile.ActivePresetId);

        // 22:30 — still watching; night mode kicks in, preset stays flat.
        SetEnvironment("2026-07-20T22:30:00+02:00", 6);
        _coordinator.Reevaluate();
        Assert.Equal("flat", _profile.ActivePresetId);
        Assert.True(_profile.Effects.NightMode.Enabled);
        Assert.Equal("night", _coordinator.LastDecision!.Evaluation.WinningRule!.Id);

        // 23:00 — movie over, back to Spotify stereo: music preset AND night mode both hold.
        SetEnvironment("2026-07-20T23:00:00+02:00", 2, "Spotify");
        _coordinator.Reevaluate();
        Assert.Equal("music", _profile.ActivePresetId);
        Assert.True(_profile.Effects.NightMode.Enabled);

        Assert.Equal(new[]
        {
            "preset=music by Music for Spotify",
            "preset=flat by Flat for movies",
            "NightMode=True by Night mode after 10pm",
            "preset=music by Music for Spotify",
        }, _log);
    }

    [Fact]
    public void PrebuiltRules_ShipDisabled_AndDoNothingUntilEnabled()
    {
        var freshProfile = new SoundstageState().GetOrCreateProfile("{d}", "X");
        Assert.Equal(5, freshProfile.Rules.Count);
        Assert.All(freshProfile.Rules, r => Assert.False(r.Enabled));
        Assert.All(freshProfile.Rules, r => Assert.True(r.IsPrebuilt));

        var result = AutomationEngine.Evaluate(
            freshProfile.Rules,
            new AudioEnvironmentSnapshot(DateTimeOffset.Parse("2026-07-20T23:00:00+02:00"), "{d}", "X", 6, 48000, 24, SpatialAudioState.Unknown, ["Spotify"]));
        Assert.Empty(result.EffectiveActions);
    }
}
