using Soundstage.Core.Effects;
using Xunit;

namespace Soundstage.Core.Tests.Effects;

public class EffectRampTests
{
    private static EffectSettings Off => EffectSettings.Default;

    [Fact]
    public void ShouldRamp_TrueForBigChange_FalseForTinyOne()
    {
        var bigNight = Off with { NightMode = new NightModeSettings(Enabled: true, Intensity: 100) };
        Assert.True(EffectRamp.ShouldRamp(Off, bigNight));

        var a = Off with { NightMode = new NightModeSettings(Enabled: true, Intensity: 50) };
        var b = Off with { NightMode = new NightModeSettings(Enabled: true, Intensity: 51) };
        Assert.False(EffectRamp.ShouldRamp(a, b));
    }

    [Fact]
    public void ShouldRamp_TrueWhenEnablingWidth()
    {
        var wide = Off with { StereoWidth = new StereoWidthSettings(Enabled: true, WidthPercent: 100) };
        Assert.True(EffectRamp.ShouldRamp(Off, wide));
    }

    [Fact]
    public void StepCount_GrowsWithMagnitude_AndStaysBounded()
    {
        var small = Off with { NightMode = new NightModeSettings(Enabled: true, Intensity: 15) };
        var big = Off with { NightMode = new NightModeSettings(Enabled: true, Intensity: 100) };

        var few = EffectRamp.StepCount(Off, small);
        var many = EffectRamp.StepCount(Off, big);

        Assert.InRange(few, 3, 12);
        Assert.InRange(many, 3, 12);
        Assert.True(many >= few);
    }

    [Fact]
    public void Lerp_EnablingAnEffect_RampsIntensityUpFromZero()
    {
        var target = Off with { NightMode = new NightModeSettings(Enabled: true, Intensity: 100) };

        var mid = EffectRamp.Lerp(Off, target, 0.5).NightMode;
        Assert.True(mid.Enabled);
        Assert.Equal(50, mid.Intensity);
    }

    [Fact]
    public void Lerp_DisablingAnEffect_StaysEnabledInteriorButRampsDown()
    {
        var from = Off with { NightMode = new NightModeSettings(Enabled: true, Intensity: 100) };

        var mid = EffectRamp.Lerp(from, Off, 0.5).NightMode;
        Assert.True(mid.Enabled);          // still present mid-ramp so the shelf shrinks smoothly
        Assert.Equal(50, mid.Intensity);   // …on its way down to zero
    }

    [Fact]
    public void Lerp_CarriesTheTargetsAmbience_Unchanged()
    {
        var from = Off with { Ambience = new AmbienceSettings(Enabled: false, Intensity: 0) };
        var to = Off with { Ambience = new AmbienceSettings(Enabled: true, Intensity: 70) };

        var mid = EffectRamp.Lerp(from, to, 0.5);
        Assert.Equal(to.Ambience, mid.Ambience);
    }

    [Fact]
    public void Lerp_WidthRampsBetweenNeutralAndTarget()
    {
        var to = Off with { StereoWidth = new StereoWidthSettings(Enabled: true, WidthPercent: 80) };

        var mid = EffectRamp.Lerp(Off, to, 0.5).StereoWidth;
        Assert.True(mid.Enabled);
        Assert.Equal(40, mid.WidthPercent);
    }
}
