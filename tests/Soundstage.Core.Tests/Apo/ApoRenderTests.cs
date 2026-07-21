using Soundstage.Core.Apo;
using Xunit;

namespace Soundstage.Core.Tests.Apo;

public class ApoRenderTests
{
    [Fact]
    public void Preamp_Renders()
    {
        Assert.Equal("Preamp: -6.4 dB", new PreampCommand(-6.4).Render());
        Assert.Equal("Preamp: 0.0 dB", new PreampCommand(0).Render());
        Assert.Equal("Preamp: 2.25 dB", new PreampCommand(2.25).Render());
    }

    [Fact]
    public void Filter_Peaking_Renders()
    {
        var f = new FilterCommand(FilterType.Peaking, 1000, -3.0, 1.41);
        Assert.Equal("Filter: ON PK Fc 1000 Hz Gain -3.0 dB Q 1.41", f.Render());
        Assert.Equal("Filter 3: ON PK Fc 1000 Hz Gain -3.0 dB Q 1.41", f.RenderNumbered(3));
    }

    [Fact]
    public void Filter_Shelves_UseQVariantCodes()
    {
        Assert.Equal(
            "Filter: ON LSC Fc 90 Hz Gain -6.5 dB Q 0.707",
            new FilterCommand(FilterType.LowShelf, 90, -6.5, 0.707).Render());
        Assert.Equal(
            "Filter: ON HSC Fc 8000 Hz Gain 2.0 dB Q 0.707",
            new FilterCommand(FilterType.HighShelf, 8000, 2, 0.707).Render());
    }

    [Fact]
    public void Filter_GainlessTypes_OmitGain()
    {
        Assert.Equal("Filter: ON LP Fc 120 Hz", new FilterCommand(FilterType.LowPass, 120).Render());
        Assert.Equal("Filter: ON HP Fc 30 Hz Q 0.5", new FilterCommand(FilterType.HighPass, 30, 0, 0.5).Render());
        Assert.Equal("Filter: OFF NO Fc 60 Hz", new FilterCommand(FilterType.Notch, 60, 0, null, Enabled: false).Render());
    }

    [Fact]
    public void GraphicEq_Renders()
    {
        var cmd = new GraphicEqCommand([new(25, -2.5), new(40, 0), new(16000, 1.75)]);
        Assert.Equal("GraphicEQ: 25 -2.5; 40 0.0; 16000 1.75", cmd.Render());
    }

    [Fact]
    public void Copy_MidSideWidthMatrix_Renders()
    {
        // width 140% → a = 1.2, b = -0.2
        var cmd = new CopyCommand([
            new CopyAssignment("L", [new(1.2, "L"), new(-0.2, "R")]),
            new CopyAssignment("R", [new(-0.2, "L"), new(1.2, "R")]),
        ]);
        // APO joins terms with '+', negatives as '+-' — a bare '-' separator gets the line dropped.
        Assert.Equal("Copy: L=1.2*L+-0.2*R R=-0.2*L+1.2*R", cmd.Render());
    }

    [Fact]
    public void Copy_UnitCoefficientAndConstant_Render()
    {
        var cmd = new CopyCommand([
            new CopyAssignment("SUB", [new(1.0, "C"), new(0.5, null)]),
        ]);
        Assert.Equal("Copy: SUB=C+0.5", cmd.Render());
    }

    [Fact]
    public void LoudnessCorrection_Renders()
    {
        Assert.Equal(
            "LoudnessCorrection: State 1 ReferenceLevel -22 Attenuation 0",
            new LoudnessCorrectionCommand(true, -22, 0).Render());
        Assert.Equal(
            "LoudnessCorrection: State 0 ReferenceLevel -20.5 Attenuation 3",
            new LoudnessCorrectionCommand(false, -20.5, 3).Render());
    }

    [Fact]
    public void VstPlugin_Renders()
    {
        Assert.Equal(
            "VSTPlugin: Library \"C:\\Plugins\\LoudMax64.dll\"",
            new VstPluginCommand(@"C:\Plugins\LoudMax64.dll").Render());
        Assert.Equal(
            "VSTPlugin: Library \"C:\\p.dll\" Thresh -12",
            new VstPluginCommand(@"C:\p.dll", "Thresh -12").Render());
    }

    [Fact]
    public void MiscCommands_Render()
    {
        Assert.Equal("Device: {a1b2c3}", new DeviceCommand("{a1b2c3}").Render());
        Assert.Equal("Channel: L R", new ChannelCommand("L R").Render());
        Assert.Equal("Include: Soundstage\\app.txt", new IncludeCommand("Soundstage\\app.txt").Render());
        Assert.Equal("Delay: 5.2 ms", new DelayCommand(5.2).Render());
        Assert.Equal("Convolution: ir\\ambience.wav", new ConvolutionCommand("ir\\ambience.wav").Render());
        Assert.Equal("# hello", new CommentCommand("hello").Render());
        Assert.Equal("#", new CommentCommand("").Render());
        Assert.Equal("", BlankLineCommand.Instance.Render());
        Assert.Equal("anything at all", new RawCommand("anything at all").Render());
    }

    [Fact]
    public void Document_NumbersFilters_PerDeviceSection()
    {
        var doc = new ApoDocument([
            new CommentCommand("generated"),
            new DeviceCommand("{dev-1}"),
            new PreampCommand(-3),
            new FilterCommand(FilterType.Peaking, 100, 1, 1.0),
            new FilterCommand(FilterType.Peaking, 200, 2, 1.0),
            new DeviceCommand("{dev-2}"),
            new FilterCommand(FilterType.Peaking, 300, 3, 1.0),
        ]);

        var lines = doc.RenderLines();
        Assert.Equal("# generated", lines[0]);
        Assert.Equal("Device: {dev-1}", lines[1]);
        Assert.Equal("Preamp: -3.0 dB", lines[2]);
        Assert.Equal("Filter 1: ON PK Fc 100 Hz Gain 1.0 dB Q 1", lines[3]);
        Assert.Equal("Filter 2: ON PK Fc 200 Hz Gain 2.0 dB Q 1", lines[4]);
        Assert.Equal("Device: {dev-2}", lines[5]);
        Assert.Equal("Filter 1: ON PK Fc 300 Hz Gain 3.0 dB Q 1", lines[6]);
    }

    [Fact]
    public void Document_Render_UsesCrlfWithTrailingNewline()
    {
        var doc = new ApoDocument([new PreampCommand(-1)]);
        Assert.Equal("Preamp: -1.0 dB\r\n", doc.Render());
    }
}
