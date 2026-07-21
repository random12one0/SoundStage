namespace Soundstage.Core.Effects;

/// <summary>
/// Generates the small stereo impulse response the ambience effect convolves with. Rather than a
/// flat noise haze (which is why the old version "didn't do much"), it models a real space: a
/// full-band <b>direct</b> impulse, a short <b>pre-delay</b> gap (so transients stay crisp and the
/// room sounds larger), a handful of discrete <b>early reflections</b>, then an exponentially
/// decaying <b>diffuse tail</b>. The wet path is high-passed (~250 Hz) so the reverb never muddies
/// the bass, and the two channels are decorrelated for width. Fully deterministic — same inputs,
/// same bytes — so tests can hash it and repeated applies never rewrite the file.
///
/// Level safety: the wet path is normalised so <c>dry + Σ|wet| == 1.0</c> per channel, keeping the
/// convolution's worst-case gain at unity — ambience needs no extra preamp headroom.
/// </summary>
public static class IrGenerator
{
    public const double MinTailSeconds = 0.12;
    public const double MaxTailSeconds = 0.35;   // a bigger space as intensity rises
    public const double PreDelaySeconds = 0.02;  // 20 ms gap between the direct sound and the reverb
    public const double WetHighPassHz = 250.0;   // keep the reverb out of the low end → no boom/mud

    private const int EarlyReflections = 8;
    private const double EarlyReflectionSpanSeconds = 0.04;

    /// <summary>Peak wet L1 (at 100% intensity); the dry impulse fills the rest so the total is unity.</summary>
    private const double MaxWetL1 = 0.5;

    private static int TailSampleCount(int sampleRate, int intensity)
    {
        var seconds = MinTailSeconds + (MaxTailSeconds - MinTailSeconds) * IntensityCurve.Fraction(Math.Clamp(intensity, 0, 100));
        return Math.Max(1, (int)(sampleRate * seconds));
    }

    /// <summary>Total frame count of the generated IR for a sample rate + intensity.</summary>
    public static int FrameCountFor(int sampleRate, int intensity) =>
        1 + (int)(sampleRate * PreDelaySeconds) + TailSampleCount(sampleRate, intensity);

    /// <summary>Builds a 32-bit float stereo WAV for the given sample rate and intensity (0–100).</summary>
    public static byte[] BuildWav(int sampleRate, int intensity)
    {
        intensity = Math.Clamp(intensity, 0, 100);
        var curve = IntensityCurve.Fraction(intensity);
        var preDelay = (int)(sampleRate * PreDelaySeconds);
        var tailSamples = TailSampleCount(sampleRate, intensity);
        var totalFrames = 1 + preDelay + tailSamples;

        var wetL1 = MaxWetL1 * curve;
        var dry = 1.0 - wetL1;

        var left = new double[totalFrames];
        var right = new double[totalFrames];

        var rngL = new XorShift(0x5057_1234u);
        var rngR = new XorShift(0xA0D1_9876u);

        // Early reflections: discrete taps just after the pre-delay, loudest first. These distinct
        // echoes are what make ambience read as a real room instead of a wash.
        var wetStart = 1 + preDelay;
        var erSpan = Math.Max(1, (int)(sampleRate * EarlyReflectionSpanSeconds));
        for (var tap = 0; tap < EarlyReflections; tap++)
        {
            var level = 1.0 - (double)tap / EarlyReflections;
            var posL = wetStart + (int)(erSpan * rngL.NextUnit());
            var posR = wetStart + (int)(erSpan * rngR.NextUnit());
            if (posL < totalFrames)
            {
                left[posL] += rngL.NextBipolar() * level;
            }

            if (posR < totalFrames)
            {
                right[posR] += rngR.NextBipolar() * level;
            }
        }

        // Diffuse tail: exponentially decaying noise (≈ −60 dB by the end of the tail).
        var decayPerSample = Math.Log(1000.0) / tailSamples;
        for (var i = wetStart; i < totalFrames; i++)
        {
            var envelope = Math.Exp(-decayPerSample * (i - wetStart));
            left[i] += rngL.NextBipolar() * envelope;
            right[i] += rngR.NextBipolar() * envelope;
        }

        // High-pass the wet path so ambience never adds boom, then normalise it to the wet L1
        // budget. The direct impulse is added afterwards and stays full-band.
        HighPass(left, sampleRate, WetHighPassHz);
        HighPass(right, sampleRate, WetHighPassHz);
        NormalizeL1(left, wetL1);
        NormalizeL1(right, wetL1);

        left[0] = dry;
        right[0] = dry;

        var leftF = new float[totalFrames];
        var rightF = new float[totalFrames];
        for (var i = 0; i < totalFrames; i++)
        {
            leftF[i] = (float)left[i];
            rightF[i] = (float)right[i];
        }

        return WriteFloatStereoWav(sampleRate, leftF, rightF);
    }

    /// <summary>Stable file name for a given sample rate + intensity (relative to the IR directory).</summary>
    public static string FileNameFor(int sampleRate, int intensity) => $"ambience-{sampleRate}-{Math.Clamp(intensity, 0, 100)}.wav";

    /// <summary>One-pole high-pass (6 dB/oct) applied in place — trims sub-bass from the reverb send.</summary>
    private static void HighPass(double[] samples, int sampleRate, double cutoffHz)
    {
        var rc = 1.0 / (2.0 * Math.PI * cutoffHz);
        var dt = 1.0 / sampleRate;
        var a = rc / (rc + dt);
        double prevIn = 0, prevOut = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var x = samples[i];
            var y = a * (prevOut + x - prevIn);
            prevIn = x;
            prevOut = y;
            samples[i] = y;
        }
    }

    /// <summary>Scales the buffer so the sum of absolute values equals <paramref name="targetL1"/>.</summary>
    private static void NormalizeL1(double[] samples, double targetL1)
    {
        double l1 = 0;
        foreach (var s in samples)
        {
            l1 += Math.Abs(s);
        }

        if (l1 <= 1e-12)
        {
            return;
        }

        var scale = targetL1 / l1;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= scale;
        }
    }

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

        private uint Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }

        public double NextBipolar() => Next() / (double)uint.MaxValue * 2.0 - 1.0;

        public double NextUnit() => Next() / (double)uint.MaxValue;
    }
}
