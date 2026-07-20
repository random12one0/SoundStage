namespace Soundstage.Core.Effects;

/// <summary>
/// Generates the tiny stereo impulse response the ambience effect convolves with: a dry
/// impulse plus a short, exponentially decaying noise tail (slightly decorrelated between
/// channels for width). Fully deterministic — same inputs, same bytes — so tests can hash
/// it and repeated applies never rewrite the file.
///
/// Level safety: dry 0.85 + tail energy scaled ≤ 0.2 keeps the convolution's worst-case
/// gain at or below unity, so ambience needs no extra headroom.
/// </summary>
public static class IrGenerator
{
    public const double TailSeconds = 0.18;

    /// <summary>Builds a 32-bit float stereo WAV for the given sample rate and intensity (0–100).</summary>
    public static byte[] BuildWav(int sampleRate, int intensity)
    {
        intensity = Math.Clamp(intensity, 0, 100);
        var tailSamples = (int)(sampleRate * TailSeconds);
        var totalFrames = 1 + tailSamples;

        var left = new float[totalFrames];
        var right = new float[totalFrames];

        // Per-channel tail L1 target: intensity 100% → 0.5. Dry is set so dry+wet == 1.0
        // (unity gain, never clips) while the wet tail is strong enough to actually hear —
        // much more audible than the previous timid 0.15 cap.
        var wetL1Target = 0.5 * IntensityCurve.Fraction(intensity);
        var dry = 1.0 - wetL1Target;
        left[0] = (float)dry;
        right[0] = (float)dry;
        var decayPerSample = Math.Log(1000.0) / tailSamples; // −60 dB by the end of the tail

        var rngL = new XorShift(0x5057_1234u);
        var rngR = new XorShift(0xA0D1_9876u);
        double energyL = 0, energyR = 0;
        for (var i = 1; i < totalFrames; i++)
        {
            var envelope = Math.Exp(-decayPerSample * i);
            left[i] = (float)(rngL.NextBipolar() * envelope);
            right[i] = (float)(rngR.NextBipolar() * envelope);
            energyL += Math.Abs(left[i]);
            energyR += Math.Abs(right[i]);
        }

        var scaleL = energyL > 0 ? wetL1Target / energyL : 0;
        var scaleR = energyR > 0 ? wetL1Target / energyR : 0;
        for (var i = 1; i < totalFrames; i++)
        {
            left[i] = (float)(left[i] * scaleL);
            right[i] = (float)(right[i] * scaleR);
        }

        return WriteFloatStereoWav(sampleRate, left, right);
    }

    /// <summary>Stable file name for a given sample rate + intensity (relative to the IR directory).</summary>
    public static string FileNameFor(int sampleRate, int intensity) => $"ambience-{sampleRate}-{Math.Clamp(intensity, 0, 100)}.wav";

    private static byte[] WriteFloatStereoWav(int sampleRate, float[] left, float[] right)
    {
        const short channels = 2;
        const short bitsPerSample = 32;
        var frames = left.Length;
        var dataBytes = frames * channels * (bitsPerSample / 8);

        // File layout: RIFF(12) + fmt(8+16) + fact(8+4, required for IEEE float) + data(8+n).
        using var stream = new MemoryStream(56 + dataBytes);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(48 + dataBytes); // total file size − 8
        writer.Write("WAVE"u8);

        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)3); // WAVE_FORMAT_IEEE_FLOAT
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);

        writer.Write("fact"u8);
        writer.Write(4);
        writer.Write(frames);

        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var i = 0; i < frames; i++)
        {
            writer.Write(left[i]);
            writer.Write(right[i]);
        }

        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Tiny deterministic PRNG (xorshift32) — no framework randomness, identical across runs.</summary>
    private struct XorShift(uint seed)
    {
        private uint _state = seed;

        public double NextBipolar()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state / (double)uint.MaxValue * 2.0 - 1.0;
        }
    }
}
