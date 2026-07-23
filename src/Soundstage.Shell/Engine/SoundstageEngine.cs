using System.Runtime.InteropServices;

namespace Soundstage.Shell.Engine;

/// <summary>EQ band shapes — must match <c>soundstage::Equalizer::BandType</c> in engine_c.h.</summary>
public enum BandType
{
    Peaking = 0,
    LowShelf = 1,
    HighShelf = 2,
    Lowpass = 3,
    Highpass = 4,
}

/// <summary>
/// Managed wrapper over the native Soundstage engine (<c>soundstage_engine.dll</c>). Owns the engine
/// handle and forwards every call across the flat C ABI. The parameter setters are safe to call from
/// the UI thread while audio is running on the audio thread — the native engine smooths every value,
/// so a live change can never click. <see cref="Process"/> is the only method meant for the audio
/// thread.
/// </summary>
public sealed class SoundstageEngine : IDisposable
{
    private const string Lib = "soundstage_engine";
    private const int ExpectedAbi = 3;

    private IntPtr _handle;
    private bool _disposed;

    /// <summary>
    /// Mirrors every parameter change out to the APO, so the plugin running inside Windows' audio
    /// engine stays in step with the in-app engine. Null until <see cref="AttachBridge"/> is called —
    /// the app is perfectly usable without the plugin installed, so this is never required.
    /// </summary>
    private ApoBridge? _bridge;

    public void AttachBridge(ApoBridge bridge)
    {
        _bridge = bridge;
        _bridge.Publish();   // send the current state immediately, not on the next change
    }

    /// <summary>
    /// Record a change against the shared settings and push it. Every setter funnels through here so
    /// there is exactly one place the two engines can drift apart, rather than forty.
    /// </summary>
    private void Mirror(Action<ApoSettings> change)
    {
        var b = _bridge;
        if (b == null)
        {
            return;
        }

        change(b.Settings);
        b.Publish();
    }

    public SoundstageEngine()
    {
        if (NativeAbiVersion() != ExpectedAbi)
        {
            throw new InvalidOperationException(
                $"soundstage_engine ABI mismatch (native {NativeAbiVersion()}, expected {ExpectedAbi}).");
        }

        _handle = ssg_create();
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to create the Soundstage engine.");
        }
    }

    /// <summary>Configure the engine for a sample rate. Call before processing; also on a rate change.</summary>
    public void Prepare(double sampleRate) => ssg_prepare(_handle, sampleRate);

    /// <summary>Clear filter/delay state (e.g. on device swap). Keeps all settings.</summary>
    public void Reset() => ssg_reset(_handle);

    /// <summary>
    /// Process one interleaved buffer on the audio thread: <paramref name="inChannels"/> in (1 or 2)
    /// to <paramref name="outChannels"/> out (2, 6, or 8), <paramref name="frames"/> sample-frames.
    /// </summary>
    public void Process(float[] input, int inChannels, float[] output, int outChannels, int frames)
        => ssg_process(_handle, input, inChannels, output, outChannels, frames);

    /// <summary>
    /// Process a source that is already multichannel, keeping its channels intact instead of folding
    /// them to stereo. Order is the Windows one: FL FR FC LFE BL BR SL SR.
    /// </summary>
    public void ProcessMulti(float[] input, int inChannels, float[] output, int outChannels, int frames)
        => ssg_process_mc(_handle, input, inChannels, output, outChannels, frames);

    // ---- master ----
    public void SetEnabled(bool on)
    {
        ssg_set_enabled(_handle, on ? 1 : 0);
        Mirror(s => s.MasterOn = on);
    }

    public void SetOutputGainDb(double db)
    {
        ssg_set_output_gain_db(_handle, db);
        Mirror(s => s.OutputGainDb = db);
    }

    // ---- per-effect on/off (each ramps, pop-free) ----
    public void EnableEq(bool on)
    {
        ssg_enable_eq(_handle, on ? 1 : 0);
        Mirror(s => s.EqOn = on);
    }

    public void EnableBass(bool on)
    {
        ssg_enable_bass(_handle, on ? 1 : 0);
        Mirror(s => s.BassOn = on);
    }

    public void EnableCompressor(bool on)
    {
        ssg_enable_compressor(_handle, on ? 1 : 0);
        Mirror(s => s.CompOn = on);
    }

    public void EnableWidth(bool on)
    {
        ssg_enable_width(_handle, on ? 1 : 0);
        Mirror(s => s.WidthOn = on);
    }

    public void EnableReverb(bool on)
    {
        ssg_enable_reverb(_handle, on ? 1 : 0);
        Mirror(s => s.ReverbOn = on);
    }

    public void EnableUpmix(bool on)
    {
        ssg_enable_upmix(_handle, on ? 1 : 0);
        Mirror(s => s.UpmixOn = on);
    }

    // ---- effect parameters ----
    public void SetEqBandCount(int count)
    {
        ssg_eq_set_num_bands(_handle, count);
        Mirror(s => s.EqBandCount = count);
    }

    public void SetEqBand(int index, BandType type, double freq, double gainDb, double q)
    {
        ssg_eq_set_band(_handle, index, (int)type, freq, gainDb, q);
        Mirror(s =>
        {
            if (index >= 0 && index < ApoBridge.MaxEqBands)
            {
                s.EqBands[index] = new ApoSettings.EqBand
                {
                    Type = (int)type, Freq = freq, GainDb = gainDb, Q = q,
                };
            }
        });
    }

    public void SetBass(double amount, double crossoverHz, double drive)
    {
        ssg_bass_set(_handle, amount, crossoverHz, drive);
        Mirror(s => { s.BassAmount = amount; s.BassCrossover = crossoverHz; s.BassDrive = drive; });
    }

    public void SetCompressor(double thresholdDb, double ratio, double kneeDb,
                              double makeupDb, double attackMs, double releaseMs)
    {
        ssg_compressor_set(_handle, thresholdDb, ratio, kneeDb, makeupDb, attackMs, releaseMs);
        Mirror(s =>
        {
            s.CompThresholdDb = thresholdDb; s.CompRatio = ratio; s.CompKneeDb = kneeDb;
            s.CompMakeupDb = makeupDb; s.CompAttackMs = attackMs; s.CompReleaseMs = releaseMs;
        });
    }

    public void SetWidth(double width)
    {
        ssg_width_set(_handle, width);
        Mirror(s => s.Width = width);
    }

    public void SetReverb(double size, double decaySeconds, double damping,
                          double preDelayMs, double width, double mix)
    {
        ssg_reverb_set(_handle, size, decaySeconds, damping, preDelayMs, width, mix);
        Mirror(s =>
        {
            s.RvSize = size; s.RvDecay = decaySeconds; s.RvDamping = damping;
            s.RvPreDelayMs = preDelayMs; s.RvWidth = width; s.RvMix = mix;
        });
    }

    public void SetUpmix(double amount, double centerGain, double lfeGain)
    {
        ssg_upmix_set(_handle, amount, centerGain, lfeGain);
        Mirror(s => { s.UpmixAmount = amount; s.UpmixCenter = centerGain; s.UpmixLfe = lfeGain; });
    }

    /// <summary>The rest of the Ambience page: input diffusion (0..1) and the reverb's band limits.</summary>
    public void SetReverbTone(double diffusion, double lowCutHz, double highCutHz)
    {
        ssg_reverb_set_tone(_handle, diffusion, lowCutHz, highCutHz);
        Mirror(s => { s.RvDiffusion = diffusion; s.RvLowCutHz = lowCutHz; s.RvHighCutHz = highCutHz; });
    }

    /// <summary>Feed the subwoofer from the stereo low end even with the upmix off.</summary>
    public void EnableSubFeed(bool on)
    {
        ssg_enable_sub_feed(_handle, on ? 1 : 0);
        Mirror(s => s.SubFeedOn = on);
    }

    /// <summary>
    /// Bass management — a receiver's "Speaker Size: Small". <paramref name="smallMask"/> has bit 0
    /// for FL through bit 7 for SR; every speaker whose bit is set loses everything below
    /// <paramref name="crossoverHz"/>, and that content goes to the subwoofer instead.
    /// </summary>
    public void SetBassManagement(bool on, double crossoverHz, int smallMask, double subGain)
    {
        ssg_bass_management(_handle, on ? 1 : 0, crossoverHz, smallMask, subGain);
        Mirror(s =>
        {
            s.BassMgmtOn = on; s.BmCrossover = crossoverHz;
            s.BmSmallMask = smallMask; s.BmSubGain = subGain;
        });
    }

    /// <summary>
    /// The output limiter. Without it the only thing stopping an over is a hard clamp, and a clamped
    /// peak is audible as a buzz rather than as loudness.
    /// </summary>
    public void SetLimiter(bool on, double ceilingDb, double releaseMs)
    {
        ssg_limiter_set(_handle, on ? 1 : 0, ceilingDb, releaseMs);
        Mirror(s => { s.LimiterOn = on; s.LimCeilingDb = ceilingDb; s.LimReleaseMs = releaseMs; });
    }

    /// <summary>How much the limiter is holding back right now, in dB.</summary>
    public double LimiterReductionDb => _handle == IntPtr.Zero ? 0.0 : ssg_limiter_reduction_db(_handle);

    /// <summary>Night mode's own dynamics stage, independent of the Leveler.</summary>
    public void EnableNight(bool on)
    {
        ssg_enable_night(_handle, on ? 1 : 0);
        Mirror(s => s.NightOn = on);
    }

    public void SetNight(double thresholdDb, double ratio, double makeupDb,
                         double attackMs, double releaseMs)
    {
        ssg_night_set(_handle, thresholdDb, ratio, makeupDb, attackMs, releaseMs);
        Mirror(s =>
        {
            s.NightThresholdDb = thresholdDb; s.NightRatio = ratio; s.NightMakeupDb = makeupDb;
            s.NightAttackMs = attackMs; s.NightReleaseMs = releaseMs;
        });
    }

    /// <summary>Early reflections (0..1) and modulation (0..1) — the reverb's character controls.</summary>
    public void SetReverbCharacter(double early, double modulation)
    {
        ssg_reverb_set_character(_handle, early, modulation);
        Mirror(s => { s.RvEarly = early; s.RvModulation = modulation; });
    }

    /// <summary>Per-speaker output trim in dB. <paramref name="channel"/> is 0..7 in 7.1 order
    /// (FL FR C LFE SL SR SBL SBR) — the calibration faders on the Speakers page.</summary>
    public void SetChannelTrimDb(int channel, double db)
    {
        ssg_set_channel_trim_db(_handle, channel, db);
        Mirror(s =>
        {
            if (channel >= 0 && channel < s.ChannelTrimDb.Length)
            {
                s.ChannelTrimDb[channel] = db;
            }
        });
    }

    // ---- meters ----
    public double GainReductionDb => _handle == IntPtr.Zero ? 0.0 : ssg_meter_reduction_db(_handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            ssg_destroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    // Static so it can be queried before a handle exists (for the version check above).
    private static int NativeAbiVersion() => ssg_abi_version();

    // ---- P/Invoke (soundstage_engine.dll on Windows, lib*.so/.dylib elsewhere) ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr ssg_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_destroy(IntPtr e);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_prepare(IntPtr e, double sampleRate);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_reset(IntPtr e);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_process(IntPtr e, float[] input, int inChannels,
                                           float[] output, int outChannels, int frames);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_process_mc(IntPtr e, float[] input, int inChannels,
                                              float[] output, int outChannels, int frames);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_set_enabled(IntPtr e, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_set_output_gain_db(IntPtr e, double db);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_enable_eq(IntPtr e, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_enable_bass(IntPtr e, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_enable_compressor(IntPtr e, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_enable_width(IntPtr e, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_enable_reverb(IntPtr e, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_enable_upmix(IntPtr e, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_eq_set_num_bands(IntPtr e, int n);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_eq_set_band(IntPtr e, int index, int type,
                                               double freq, double gainDb, double q);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_bass_set(IntPtr e, double amount, double crossoverHz, double drive);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_compressor_set(IntPtr e, double thresholdDb, double ratio, double kneeDb,
                                                  double makeupDb, double attackMs, double releaseMs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_width_set(IntPtr e, double width);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_reverb_set(IntPtr e, double size, double decaySeconds, double damping,
                                              double preDelayMs, double width, double mix);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_upmix_set(IntPtr e, double amount, double centerGain, double lfeGain);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_reverb_set_tone(IntPtr e, double diffusion,
                                                   double lowCutHz, double highCutHz);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_enable_sub_feed(IntPtr e, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_bass_management(IntPtr e, int on, double crossoverHz,
                                                   int smallMask, double subGain);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_limiter_set(IntPtr e, int on, double ceilingDb, double releaseMs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern double ssg_limiter_reduction_db(IntPtr e);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_enable_night(IntPtr e, int on);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_night_set(IntPtr e, double thresholdDb, double ratio,
                                             double makeupDb, double attackMs, double releaseMs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_reverb_set_character(IntPtr e, double early, double modulation);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_set_channel_trim_db(IntPtr e, int ch, double db);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern double ssg_meter_reduction_db(IntPtr e);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ssg_abi_version();
}
