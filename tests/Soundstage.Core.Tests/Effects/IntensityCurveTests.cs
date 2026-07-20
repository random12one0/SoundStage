using Soundstage.Core.Effects;
using Xunit;

namespace Soundstage.Core.Tests.Effects;

public class IntensityCurveTests
{
    [Fact]
    public void Endpoints_AreExact()
    {
        Assert.Equal(0.0, IntensityCurve.Fraction(0), 5);
        Assert.Equal(1.0, IntensityCurve.Fraction(100), 5);
    }

    [Fact]
    public void FiftyPercent_DeliversMoreThanTwoThirds()
    {
        // The whole point: the middle of the slider is the sweet spot, not a timid half.
        var half = IntensityCurve.Fraction(50);
        Assert.InRange(half, 0.66, 0.75);
    }

    [Fact]
    public void Curve_IsMonotonic_AndFrontLoaded()
    {
        double previous = -1;
        for (var i = 0; i <= 100; i += 10)
        {
            var v = IntensityCurve.Fraction(i);
            Assert.True(v >= previous, "must never decrease");
            previous = v;
        }

        // Early gain (0→50) exceeds late gain (50→100).
        var early = IntensityCurve.Fraction(50) - IntensityCurve.Fraction(0);
        var late = IntensityCurve.Fraction(100) - IntensityCurve.Fraction(50);
        Assert.True(early > late);
    }

    [Fact]
    public void OutOfRange_IsClamped()
    {
        Assert.Equal(0.0, IntensityCurve.Fraction(-20), 5);
        Assert.Equal(1.0, IntensityCurve.Fraction(200), 5);
    }
}
