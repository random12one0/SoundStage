using System.Globalization;
using Soundstage.Core.Apo;
using Xunit;

namespace Soundstage.Core.Tests.Apo;

/// <summary>
/// APO configs must always use '.' decimals. These tests run rendering and parsing under
/// comma-decimal and Turkish cultures — historically the top bug class in EQ tools.
/// </summary>
public class CultureSafetyTests
{
    private static void RunUnder(string cultureName, Action action)
    {
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            var culture = new CultureInfo(cultureName);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            action();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    [InlineData("fr-FR")]
    public void Rendering_IsCultureInvariant(string culture)
    {
        RunUnder(culture, () =>
        {
            var doc = new ApoDocument([
                new PreampCommand(-6.4),
                new FilterCommand(FilterType.Peaking, 1234.5, -3.25, 1.41),
                new GraphicEqCommand([new(31.5, -2.5)]),
                new CopyCommand([new CopyAssignment("L", [new(1.15, "L"), new(-0.15, "R")])]),
                new LoudnessCorrectionCommand(true, -22.5, 0),
            ]);

            var text = doc.Render();
            Assert.Equal(
                "Preamp: -6.4 dB\r\n" +
                "Filter 1: ON PK Fc 1234.5 Hz Gain -3.25 dB Q 1.41\r\n" +
                "GraphicEQ: 31.5 -2.5\r\n" +
                "Copy: L=1.15*L-0.15*R\r\n" +
                "LoudnessCorrection: State 1 ReferenceLevel -22.5 Attenuation 0\r\n",
                text);
        });
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void Parsing_IsCultureInvariant(string culture)
    {
        RunUnder(culture, () =>
        {
            var result = ApoConfigParser.Parse("Filter 1: ON PK Fc 1234.5 Hz Gain -3.25 dB Q 1.41\r\n");
            var filter = Assert.IsType<FilterCommand>(Assert.Single(result.Document.Commands));
            Assert.Equal(1234.5, filter.FrequencyHz);
            Assert.Equal(-3.25, filter.GainDb);
            Assert.Equal(1.41, filter.Q);
        });
    }

    [Fact]
    public void TurkishI_DoesNotBreakKeywordMatching()
    {
        // 'FILTER'.ToLower() in tr-TR yields 'fılter' (dotless i) — ensure we never do that.
        RunUnder("tr-TR", () =>
        {
            var result = ApoConfigParser.Parse("FILTER: ON PK Fc 100 Hz Gain 1 dB Q 1\r\nInclude: x.txt\r\n");
            Assert.IsType<FilterCommand>(result.Document.Commands[0]);
            Assert.IsType<IncludeCommand>(result.Document.Commands[1]);
        });
    }
}
