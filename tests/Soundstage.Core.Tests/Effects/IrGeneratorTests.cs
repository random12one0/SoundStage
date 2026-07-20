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
    public void Wav_LengthMatchesTailDuration()
    {
        const int rate = 44100;
        var bytes = IrGenerator.BuildWav(rate, 50);
        var expectedFrames = 1 + (int)(rate * IrGenerator.TailSeconds);
        // data chunk is the last one: 8-byte header + frames*2ch*4bytes.
        var dataBytes = expectedFrames * 2 * 4;
        Assert.Equal(56 + dataBytes, bytes.Length);
    }

    [Fact]
    public void Samples_NeverExceedUnity_AndTotalGainIsSafe()
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

            // Worst-case convolution gain (L1 norm) stays at or below unity per channel.
            Assert.True(l1Left <= 1.0 + 1e-3, $"L1 left {l1Left} at intensity {intensity}");
            Assert.True(l1Right <= 1.0 + 1e-3, $"L1 right {l1Right} at intensity {intensity}");
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
    public void FileName_IsStablePerRateAndIntensity()
    {
        Assert.Equal("ambience-48000-30.wav", IrGenerator.FileNameFor(48000, 30));
        Assert.Equal("ambience-44100-100.wav", IrGenerator.FileNameFor(44100, 150));
    }
}
