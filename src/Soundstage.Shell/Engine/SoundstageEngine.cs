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
    private const int ExpectedAbi = 2;

    private IntPtr _handle;
    private bool _disposed;

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

    // ---- master ----
    public void SetEnabled(bool on) => ssg_set_enabled(_handle, on ? 1 : 0);
    public void SetOutputGainDb(double db) => ssg_set_output_gain_db(_handle, db);

    // ---- per-effect on/off (each ramps, pop-free) ----
    public void EnableEq(bool on) => ssg_enable_eq(_handle, on ? 1 : 0);
    public void EnableBass(bool on) => ssg_enable_bass(_handle, on ? 1 : 0);
    public void EnableCompressor(bool on) => ssg_enable_compressor(_handle, on ? 1 : 0);
    public void EnableWidth(bool on) => ssg_enable_width(_handle, on ? 1 : 0);
    public void EnableReverb(bool on) => ssg_enable_reverb(_handle, on ? 1 : 0);
    public void EnableUpmix(bool on) => ssg_enable_upmix(_handle, on ? 1 : 0);

    // ---- effect parameters ----
    public void SetEqBandCount(int count) => ssg_eq_set_num_bands(_handle, count);

    public void SetEqBand(int index, BandType type, double freq, double gainDb, double q)
        => ssg_eq_set_band(_handle, index, (int)type, freq, gainDb, q);

    public void SetBass(double amount, double crossoverHz, double drive)
        => ssg_bass_set(_handle, amount, crossoverHz, drive);

    public void SetCompressor(double thresholdDb, double ratio, double kneeDb,
                              double makeupDb, double attackMs, double releaseMs)
        => ssg_compressor_set(_handle, thresholdDb, ratio, kneeDb, makeupDb, attackMs, releaseMs);

    public void SetWidth(double width) => ssg_width_set(_handle, width);

    public void SetReverb(double size, double decaySeconds, double damping,
                          double preDelayMs, double width, double mix)
        => ssg_reverb_set(_handle, size, decaySeconds, damping, preDelayMs, width, mix);

    public void SetUpmix(double amount, double centerGain, double lfeGain)
        => ssg_upmix_set(_handle, amount, centerGain, lfeGain);

    /// <summary>The rest of the Ambience page: input diffusion (0..1) and the reverb's band limits.</summary>
    public void SetReverbTone(double diffusion, double lowCutHz, double highCutHz)
        => ssg_reverb_set_tone(_handle, diffusion, lowCutHz, highCutHz);

    /// <summary>Early reflections (0..1) and modulation (0..1) — the reverb's character controls.</summary>
    public void SetReverbCharacter(double early, double modulation)
        => ssg_reverb_set_character(_handle, early, modulation);

    /// <summary>Per-speaker output trim in dB. <paramref name="channel"/> is 0..7 in 7.1 order
    /// (FL FR C LFE SL SR SBL SBR) — the calibration faders on the Speakers page.</summary>
    public void SetChannelTrimDb(int channel, double db) => ssg_set_channel_trim_db(_handle, channel, db);

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
    private static extern void ssg_reverb_set_character(IntPtr e, double early, double modulation);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void ssg_set_channel_trim_db(IntPtr e, int ch, double db);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern double ssg_meter_reduction_db(IntPtr e);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int ssg_abi_version();
}
