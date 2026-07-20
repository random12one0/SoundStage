using Soundstage.Core.Apo;
using Soundstage.Core.Dsp;
using Xunit;

namespace Soundstage.Core.Tests.Dsp;

public class BiquadTests
{
    private const double Fs = 48000;

    [Fact]
    public void Peaking_HitsRequestedGain_AtCenter()
    {
        var c = Biquad.Design(FilterType.Peaking, Fs, 1000, 6.0, 1.0);
        Assert.Equal(6.0, Biquad.MagnitudeDb(c, Fs, 1000), 1);
        // Far away it should be near flat.
        Assert.Equal(0.0, Biquad.MagnitudeDb(c, Fs, 60), 0);
        Assert.Equal(0.0, Biquad.MagnitudeDb(c, Fs, 15000), 0);
    }

    [Fact]
    public void Peaking_Cut_IsSymmetric()
    {
        var boost = Biquad.Design(FilterType.Peaking, Fs, 500, 4.0, 1.4);
        var cut = Biquad.Design(FilterType.Peaking, Fs, 500, -4.0, 1.4);
        Assert.Equal(
            Biquad.MagnitudeDb(boost, Fs, 700),
            -Biquad.MagnitudeDb(cut, Fs, 700),
            2);
    }

    [Fact]
    public void LowShelf_ReachesGain_BelowCorner_AndFlatAbove()
    {
        var c = Biquad.Design(FilterType.LowShelf, Fs, 100, -6.0, 0.707);
        Assert.Equal(-6.0, Biquad.MagnitudeDb(c, Fs, 20), 1);
        Assert.Equal(0.0, Biquad.MagnitudeDb(c, Fs, 4000), 1);
        // At the corner, a shelf sits at half gain.
        Assert.Equal(-3.0, Biquad.MagnitudeDb(c, Fs, 100), 0);
    }

    [Fact]
    public void HighShelf_ReachesGain_AboveCorner()
    {
        var c = Biquad.Design(FilterType.HighShelf, Fs, 8000, 3.0, 0.707);
        Assert.Equal(3.0, Biquad.MagnitudeDb(c, Fs, 19000), 1);
        Assert.Equal(0.0, Biquad.MagnitudeDb(c, Fs, 500), 1);
    }

    [Fact]
    public void LowPass_Is3dBDown_AtCutoff()
    {
        var c = Biquad.Design(FilterType.LowPass, Fs, 1000, 0, 0.7071);
        Assert.Equal(-3.0, Biquad.MagnitudeDb(c, Fs, 1000), 0);
        Assert.True(Biquad.MagnitudeDb(c, Fs, 8000) < -30);
    }

    [Fact]
    public void Notch_IsDeep_AtCenter_FlatElsewhere()
    {
        var c = Biquad.Design(FilterType.Notch, Fs, 1000, 0, 5);
        Assert.True(Biquad.MagnitudeDb(c, Fs, 1000) < -25);
        Assert.Equal(0.0, Biquad.MagnitudeDb(c, Fs, 100), 1);
    }

    [Fact]
    public void AllPass_IsFlat()
    {
        var c = Biquad.Design(FilterType.AllPass, Fs, 1000, 0, 1);
        Assert.Equal(0.0, Biquad.MagnitudeDb(c, Fs, 100), 2);
        Assert.Equal(0.0, Biquad.MagnitudeDb(c, Fs, 1000), 2);
        Assert.Equal(0.0, Biquad.MagnitudeDb(c, Fs, 12000), 2);
    }

    [Fact]
    public void ExtremeInputs_AreClamped_NotNaN()
    {
        var c = Biquad.Design(FilterType.Peaking, Fs, 50000, 12, 0.001);
        var db = Biquad.MagnitudeDb(c, Fs, 30000);
        Assert.False(double.IsNaN(db));
        Assert.False(double.IsInfinity(db));
    }
}
