namespace Soundstage.Core.Effects;

/// <summary>
/// Generates the small <b>mono</b> impulse response the ambience effect convolves with. Mono is
/// deliberate and important: Equalizer APO applies a single-channel IR to <b>every</b> output
/// channel, so the reverb works identically on 2.0, 5.1 and 7.1 — a stereo (2-channel) IR, by
/// contrast, can't be mapped onto a 7.1 stream and APO silently drops it (that was the real
/// "ambience does nothing on my receiver" bug). It models a real space: a full-band <b>direct</b>
/// impulse, a short <b>pre-delay</b> gap, a handful of discrete <b>early reflections</b>, then an
/// exponentially decaying <b>diffuse tail</b>. The wet path is high-passed (~180 Hz) so the reverb
/// never muddies the bass (and so the LFE channel stays effectively dry). Fully deterministic —
/// same inputs, same bytes — so repeated applies never rewrite the file.
///
/// Level safety: the wet path is normalised so <c>dry + Σ|wet| == 1.0 + wet</c>, and the extra wet
/// energy is paid for with preamp headroom (<see cref="ExtraHeadroomDbFor"/>).
/// </summary>
public static class IrGenerator
{
    /// <summary>
    /// Bumped whenever the IR-generation algorithm changes. It is part of the cache file name so a
    /// new algorithm always writes a NEW file instead of silently reusing a stale one on disk. This
    /// is the fix for "ambience does nothing after an update": the old, faint IR was cached by name
    /// and never regenerated. Bump this any time the tail/level/shape below changes audibly.
    /// </summary>
    public const int Version = 4;

    public const double MinTailSeconds = 0.8;    // even the gentlest setting has a clearly audible tail
    public const double MaxTailSeconds = 2.6;    // a big hall that rings out for a couple of seconds after the music stops
    public const double PreDelaySeconds = 0.02;  // 20 ms gap between the direct sound and the reverb
    public const double WetHighPassHz = 180.0;   // keep the reverb out of the deep bass → no mud

    private const int EarlyReflections = 12;
    private const double EarlyReflectionSpanSeconds = 0.05;

    /// <summary>
    /// Peak wet L1 (at 100% intensity). The dry impulse stays at full level (1.0) so ambience
    /// doesn't just sound "quieter" — the wet reverb is added ON TOP, and the extra energy is
    /// paid for with preamp headroom (<see cref="ExtraHeadroomDbFor"/>) rather than by ducking
    /// the dry signal. This is what makes it an obvious, hearable reverb instead of a faint haze.
    /// </summary>
    public const double MaxWetL1 = 1.0;

    private static int TailSampleCount(int sampleRate, int intensity)
    {
        var seconds = MinTailSeconds + (MaxTailSeconds - MinTailSeconds) * IntensityCurve.Fraction(Math.Clamp(intensity, 0, 100));
        return Math.Max(1, (int)(sampleRate * seconds));
    }

    /// <summary>Total frame count of the generated IR for a sample rate + intensity.</summary>
    public static int FrameCountFor(int sampleRate, int intensity) =>
        1 + (int)(sampleRate * PreDelaySeconds) + TailSampleCount(sampleRate, intensity);

    /// <summary>
    /// Extra preamp headroom (dB) the ambience mix needs so the wet-on-top reverb can't clip:
    /// the convolution's worst-case gain is the IR's L1 = dry(1.0) + wet. 0 at 0% intensity.
    /// </summary>
    public static double ExtraHeadroomDbFor(int intensity) =>
        20.0 * Math.Log10(1.0 + MaxWetL1 * IntensityCurve.Fraction(Math.Clamp(intensity, 0, 100)));

    /// <summary>Builds a 32-bit float <b>mono</b> WAV for the given sample rate and intensity (0–100).</summary>
    public static byte[] BuildWav(int sampleRate, int intensity)
    {
        intensity = Math.Clamp(intensity, 0, 100);
        var curve = IntensityCurve.Fraction(intensity);
        var preDelay = (int)(sampleRate * PreDelaySeconds);
        var tailSamples = TailSampleCount(sampleRate, intensity);
        var totalFrames = 1 + preDelay + tailSamples;

        var wetL1 = MaxWetL1 * curve;
        var dry = 1.0; // keep the direct sound at full level; the wet reverb is added on top

        var samples = new double[totalFrames];
        var rng = new XorShift(0x5057_1234u);

        // Early reflections: discrete taps just after the pre-delay, loudest first. These distinct
        // echoes are what make ambience read as a real room instead of a wash.
        var wetStart = 1 + preDelay;
        var erSpan = Math.Max(1, (int)(sampleRate * EarlyReflectionSpanSeconds));
        for (var tap = 0; tap < EarlyReflections; tap++)
        {
            var level = 1.0 - (double)tap / EarlyReflections;
            var pos = wetStart + (int)(erSpan * rng.NextUnit());
            if (pos < totalFrames)
            {
                samples[pos] += rng.NextBipolar() * level;
            }
        }

        // Diffuse tail: exponentially decaying noise (≈ −60 dB by the end of the tail).
        var decayPerSample = Math.Log(1000.0) / tailSamples;
        for (var i = wetStart; i < totalFrames; i++)
        {
            var envelope = Math.Exp(-decayPerSample * (i - wetStart));
            samples[i] += rng.NextBipolar() * envelope;
        }

        // High-pass the wet path so ambience never adds boom, then normalise it to the wet L1
        // budget. The direct impulse is added afterwards and stays full-band.
        HighPass(samples, sampleRate, WetHighPassHz);
        NormalizeL1(samples, wetL1);

        samples[0] = dry;

        var mono = new float[totalFrames];
        for (var i = 0; i < totalFrames; i++)
        {
            mono[i] = (float)samples[i];
        }

        return WriteFloatMonoWav(sampleRate, mono);
    }

    /// <summary>
    /// Stable file name for a given sample rate + intensity (relative to the IR directory). The
    /// <see cref="Version"/> token means a changed algorithm lands as a brand-new file — old caches
    /// are never mistaken for the current IR.
    /// </summary>
    public static string FileNameFor(int sampleRate, int intensity) => $"ambience-v{Version}-{sampleRate}-{Math.Clamp(intensity, 0, 100)}.wav";

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

    private static byte[] WriteFloatMonoWav(int sampleRate, float[] samples)
    {
        const short channels = 1;
        const short bitsPerSample = 32;
        var frames = samples.Length;
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
        foreach (var sample in samples)
        {
            writer.Write(sample);
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
