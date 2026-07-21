using System.Security.Cryptography;
using Soundstage.Core.Effects;
using Xunit;

namespace Soundstage.Core.Tests.Effects;

public class IrGeneratorTests
{
    [Fact]
    public void Wav_IsDeterministic()
    {
        var a = IrGenerator.BuildWav(48000, 40);
        var b = IrGenerator.BuildWav(48000, 40);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(a)), Convert.ToHexString(SHA256.HashData(b)));
    }

    [Fact]
    public void Wav_HasValidRiffStructure()
    {
        var bytes = IrGenerator.BuildWav(48000, 30);

        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
        // Declared RIFF size matches actual file length − 8.
        var riffSize = BitConverter.ToInt32(bytes, 4);
        Assert.Equal(bytes.Length - 8, riffSize);

        // fmt: IEEE float, stereo, requested rate.
        Assert.Equal("fmt "u8.ToArray(), bytes[12..16]);
        Assert.Equal(3, BitConverter.ToInt16(bytes, 20));
        Assert.Equal(2, BitConverter.ToInt16(bytes, 22));
        Assert.Equal(48000, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(32, BitConverter.ToInt16(bytes, 34));
    }

    [Fact]
    public void Wav_LengthMatchesGeneratedFrameCount()
    {
        const int rate = 44100;
        var bytes = IrGenerator.BuildWav(rate, 50);
        var expectedFrames = IrGenerator.FrameCountFor(rate, 50);
        // data chunk is the last one: 8-byte header + frames*2ch*4bytes.
        var dataBytes = expectedFrames * 2 * 4;
        Assert.Equal(56 + dataBytes, bytes.Length);
    }

    [Fact]
    public void HigherIntensity_ProducesALongerTail()
    {
        Assert.True(IrGenerator.FrameCountFor(48000, 100) > IrGenerator.FrameCountFor(48000, 20));
    }

    [Fact]
    public void Samples_NeverExceedUnity_AndRequestedHeadroomKeepsItSafe()
    {
        foreach (var intensity in new[] { 0, 50, 100 })
        {
            var bytes = IrGenerator.BuildWav(48000, intensity);
            double l1Left = 0, l1Right = 0;
            var frames = (bytes.Length - 56) / 8;
            for (var i = 0; i < frames; i++)
            {
                var left = BitConverter.ToSingle(bytes, 56 + i * 8);
                var right = BitConverter.ToSingle(bytes, 56 + i * 8 + 4);
                Assert.True(Math.Abs(left) <= 1.0f);
                Assert.True(Math.Abs(right) <= 1.0f);
                l1Left += Math.Abs(left);
                l1Right += Math.Abs(right);
            }

            // The wet reverb is added on top of a full-level dry, so per-channel L1 exceeds 1;
            // the headroom the effect requests must bring the worst-case gain back to ≤ unity.
            var afterHeadroom = Math.Pow(10, -IrGenerator.ExtraHeadroomDbFor(intensity) / 20.0);
            Assert.True(l1Left * afterHeadroom <= 1.0 + 1e-3, $"L1 left {l1Left} at intensity {intensity}");
            Assert.True(l1Right * afterHeadroom <= 1.0 + 1e-3, $"L1 right {l1Right} at intensity {intensity}");
        }
    }

    [Fact]
    public void ZeroIntensity_IsDryOnly()
    {
        var bytes = IrGenerator.BuildWav(48000, 0);
        var frames = (bytes.Length - 56) / 8;
        for (var i = 1; i < frames; i++)
        {
            Assert.Equal(0, BitConverter.ToSingle(bytes, 56 + i * 8));
        }
    }

    [Fact]
    public void FileName_IsStablePerRateAndIntensity_AndCarriesTheVersion()
    {
        // The version token means a reworked IR always lands as a new file — the fix for
        // "ambience does nothing after an update" (a stale cached IR being reused by name).
        Assert.Equal($"ambience-v{IrGenerator.Version}-48000-30.wav", IrGenerator.FileNameFor(48000, 30));
        Assert.Equal($"ambience-v{IrGenerator.Version}-44100-100.wav", IrGenerator.FileNameFor(44100, 150));
    }

    [Fact]
    public void Tail_RingsOutForSeconds_AtHighIntensity()
    {
        // "If I pause the music it should reverb for a second or two" — the tail must be long.
        var seconds = IrGenerator.FrameCountFor(48000, 100) / 48000.0;
        Assert.True(seconds >= 2.0, $"tail was only {seconds:0.00}s");
    }
}
