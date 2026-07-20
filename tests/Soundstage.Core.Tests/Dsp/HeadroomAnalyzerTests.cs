using Soundstage.Core.Apo;
using Soundstage.Core.Dsp;
using Xunit;

namespace Soundstage.Core.Tests.Dsp;

public class HeadroomAnalyzerTests
{
    [Fact]
    public void SinglePeak_RecommendsTrim_ToMargin()
    {
        var report = HeadroomAnalyzer.Analyze(0, [new FilterCommand(FilterType.Peaking, 1000, 6.0, 1.0)]);

        Assert.Equal(6.0, report.PeakBoostDb, 1);
        Assert.InRange(report.PeakFrequencyHz, 900, 1100);
        Assert.Equal(-6.5, report.RecommendedPreampDb, 1);
        Assert.True(report.WouldClipWithoutTrim);
        Assert.Equal(0.5, report.HeadroomDb, 1);
    }

    [Fact]
    public void OverlappingBoosts_Sum()
    {
        var report = HeadroomAnalyzer.Analyze(0, [
            new FilterCommand(FilterType.Peaking, 1000, 3.0, 1.0),
            new FilterCommand(FilterType.Peaking, 1000, 3.0, 1.0),
        ]);

        Assert.Equal(6.0, report.PeakBoostDb, 1);
    }

    [Fact]
    public void CutsOnly_NeedNoTrim_AndKeepAuthorPreamp()
    {
        var report = HeadroomAnalyzer.Analyze(-2, [
            new FilterCommand(FilterType.Peaking, 300, -4.0, 1.0),
            new FilterCommand(FilterType.LowShelf, 90, -6.0, 0.707),
        ]);

        Assert.Equal(0.0, report.PeakBoostDb, 1);
        Assert.Equal(-2, report.RecommendedPreampDb, 2);
        Assert.False(report.WouldClipWithoutTrim);
        Assert.Equal(0, report.AutoTrimDb, 2);
    }

    [Fact]
    public void GenerousAuthorPreamp_IsNeverRaised()
    {
        var report = HeadroomAnalyzer.Analyze(-12, [new FilterCommand(FilterType.Peaking, 1000, 6.0, 1.0)]);
        Assert.Equal(-12, report.RecommendedPreampDb, 2);
        Assert.Equal(0, report.AutoTrimDb, 2);
    }

    [Fact]
    public void DisabledFilters_AreIgnored()
    {
        var report = HeadroomAnalyzer.Analyze(0, [new FilterCommand(FilterType.Peaking, 1000, 12.0, 1.0, Enabled: false)]);
        Assert.Equal(0, report.PeakBoostDb, 1);
    }

    [Fact]
    public void GraphicPoints_AreInterpolated()
    {
        var report = HeadroomAnalyzer.Analyze(0, [], graphicPoints:
        [
            new GraphicEqPoint(100, 0),
            new GraphicEqPoint(1000, 4),
            new GraphicEqPoint(10000, 0),
        ]);

        Assert.Equal(4.0, report.PeakBoostDb, 1);
        Assert.InRange(report.PeakFrequencyHz, 800, 1200);
    }

    [Fact]
    public void BroadbandGain_AddsToPeak()
    {
        var report = HeadroomAnalyzer.Analyze(0, [new FilterCommand(FilterType.Peaking, 1000, 3.0, 1.0)], broadbandGainsDb: [3.5]);
        Assert.Equal(6.5, report.PeakBoostDb, 1);
    }

    [Fact]
    public void FlatChain_StaysBitTransparent_NoMarginTrim()
    {
        var report = HeadroomAnalyzer.Analyze(0, []);
        Assert.Equal(0, report.PeakBoostDb, 3);
        Assert.Equal(0, report.RecommendedPreampDb, 3);
        Assert.False(report.WouldClipWithoutTrim);
    }
}
