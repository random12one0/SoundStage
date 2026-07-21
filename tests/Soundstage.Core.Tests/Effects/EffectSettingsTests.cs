using System.Text.Json;
using Soundstage.Core;
using Soundstage.Core.Effects;
using Xunit;

namespace Soundstage.Core.Tests.Effects;

public class EffectSettingsTests
{
    [Fact]
    public void Deserialize_SettingsWrittenBeforeFidelity_DefaultsIt_InsteadOfNull()
    {
        // A settings blob from a build that predates the Fidelity effect — the exact crash-on-upgrade
        // case (a positional record would have set Fidelity to null and NRE'd on the first read).
        const string oldJson =
            """
            {"nightMode":{"enabled":true,"intensity":60},"loudness":{"enabled":false},"stereoWidth":{"enabled":false,"widthPercent":50},"ambience":{"enabled":false,"intensity":50}}
            """;

        var effects = JsonSerializer.Deserialize<EffectSettings>(oldJson, JsonDefaults.Readable);

        Assert.NotNull(effects);
        Assert.NotNull(effects!.Fidelity);        // the missing effect must default, not be null
        Assert.False(effects.Fidelity.Enabled);
        Assert.NotNull(effects.Ambience);
        Assert.True(effects.NightMode.Enabled);   // present fields still load
        Assert.Equal(60, effects.NightMode.Intensity);
    }

    [Fact]
    public void Default_HasEveryEffectNonNull()
    {
        var d = EffectSettings.Default;
        Assert.NotNull(d.NightMode);
        Assert.NotNull(d.Loudness);
        Assert.NotNull(d.StereoWidth);
        Assert.NotNull(d.Ambience);
        Assert.NotNull(d.Fidelity);
    }
}
