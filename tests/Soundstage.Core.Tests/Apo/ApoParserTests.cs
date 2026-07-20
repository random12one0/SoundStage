using Soundstage.Core.Apo;
using Xunit;

namespace Soundstage.Core.Tests.Apo;

public class ApoParserTests
{
    [Fact]
    public void Parses_AutoEqStyleParametricFile()
    {
        // Representative of AutoEq's ParametricEQ.txt output format.
        const string text =
            "Preamp: -6.4 dB\n" +
            "Filter 1: ON LSC Fc 105 Hz Gain 3.4 dB Q 0.70\n" +
            "Filter 2: ON PK Fc 1274 Hz Gain -2.6 dB Q 1.62\n" +
            "Filter 3: ON HSC Fc 10000 Hz Gain -4.2 dB Q 0.70\n";

        var result = ApoConfigParser.Parse(text);

        Assert.Empty(result.Warnings);
        var preamp = Assert.IsType<PreampCommand>(result.Document.Commands[0]);
        Assert.Equal(-6.4, preamp.Db);

        var f1 = Assert.IsType<FilterCommand>(result.Document.Commands[1]);
        Assert.Equal(FilterType.LowShelf, f1.Type);
        Assert.Equal(105, f1.FrequencyHz);
        Assert.Equal(3.4, f1.GainDb);
        Assert.Equal(0.70, f1.Q);
        Assert.True(f1.Enabled);

        var f3 = Assert.IsType<FilterCommand>(result.Document.Commands[3]);
        Assert.Equal(FilterType.HighShelf, f3.Type);
    }

    [Fact]
    public void Parses_FixedSlopeShelfVariant()
    {
        var result = ApoConfigParser.Parse("Filter 1: ON LS 6dB Fc 100 Hz Gain 2 dB\n");
        var filter = Assert.IsType<FilterCommand>(Assert.Single(result.Document.Commands));
        Assert.Equal(FilterType.LowShelf, filter.Type);
        Assert.Equal(100, filter.FrequencyHz);
        Assert.Equal(2, filter.GainDb);
        Assert.Null(filter.Q);
    }

    [Fact]
    public void Parses_OffFilters()
    {
        var result = ApoConfigParser.Parse("Filter 1: OFF PK Fc 500 Hz Gain 1 dB Q 2\n");
        var filter = Assert.IsType<FilterCommand>(Assert.Single(result.Document.Commands));
        Assert.False(filter.Enabled);
    }

    [Fact]
    public void Parses_GraphicEq()
    {
        var result = ApoConfigParser.Parse("GraphicEQ: 25 -2.5; 40 0; 16000 1.75\n");
        var geq = Assert.IsType<GraphicEqCommand>(Assert.Single(result.Document.Commands));
        Assert.Equal(3, geq.Points.Count);
        Assert.Equal(25, geq.Points[0].FrequencyHz);
        Assert.Equal(-2.5, geq.Points[0].GainDb);
    }

    [Fact]
    public void Parses_CommentsBlanksDevicesIncludes()
    {
        const string text = "# a comment\n\nDevice: Speakers {guid}\nInclude: extra.txt\n";
        var result = ApoConfigParser.Parse(text);

        Assert.Equal("a comment", Assert.IsType<CommentCommand>(result.Document.Commands[0]).Text);
        Assert.IsType<BlankLineCommand>(result.Document.Commands[1]);
        Assert.Equal("Speakers {guid}", Assert.IsType<DeviceCommand>(result.Document.Commands[2]).MatchSpec);
        Assert.Equal("extra.txt", Assert.IsType<IncludeCommand>(result.Document.Commands[3]).Path);
    }

    [Fact]
    public void UnknownAndMalformedLines_ArePreservedVerbatim()
    {
        const string text =
            "Copy: L=0.5*L+0.5*R\n" +          // structured type we render but intentionally don't parse
            "Eval: 1+1\n" +                     // unknown command
            "Filter 1: ON XX Fc 100 Hz\n" +     // unknown filter type
            "this is not a config line\n";      // garbage

        var result = ApoConfigParser.Parse(text);

        Assert.All(result.Document.Commands, c => Assert.IsType<RawCommand>(c));
        Assert.Equal(2, result.Warnings.Count); // unknown type + garbage line warn; Copy/Eval pass silently
    }

    [Fact]
    public void BandwidthFilters_ArePreservedNotEditable()
    {
        var result = ApoConfigParser.Parse("Filter 1: ON PK Fc 100 Hz Gain 1 dB BW Oct 0.5\n");
        Assert.IsType<RawCommand>(Assert.Single(result.Document.Commands));
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void RoundTrip_StructuredCommands_AreStable()
    {
        const string original =
            "Preamp: -6.4 dB\r\n" +
            "Filter 1: ON LSC Fc 105 Hz Gain 3.4 dB Q 0.7\r\n" +
            "Filter 2: ON PK Fc 1274 Hz Gain -2.6 dB Q 1.62\r\n" +
            "GraphicEQ: 25 -2.5; 16000 1.75\r\n";

        var parsed = ApoConfigParser.Parse(original);
        var rendered = parsed.Document.Render();
        Assert.Equal(original, rendered);

        // A second round-trip must be byte-identical (fixed point).
        Assert.Equal(rendered, ApoConfigParser.Parse(rendered).Document.Render());
    }

    [Fact]
    public void Parse_EmptyAndWhitespaceInput_Yields_NoCommands()
    {
        Assert.Empty(ApoConfigParser.Parse("").Document.Commands);
        Assert.Single(ApoConfigParser.Parse("\n").Document.Commands); // one blank line
    }
}
