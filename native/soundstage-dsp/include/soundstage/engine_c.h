/* Soundstage engine — flat C ABI.
 *
 * This is the boundary the app talks to. The DSP is C++ (EngineChain and its effects), but the app
 * shell is C#/.NET, so the engine ships as a small native library (soundstage_engine.dll on Windows,
 * .so/.dylib elsewhere) exposing plain C functions the app P/Invokes: create an engine, prepare it
 * for a sample rate, push each audio buffer through it, and set a knob. No C++ types cross the line,
 * so any language with a C FFI can drive it.
 *
 * Threading: create/prepare/destroy on the control thread; ssg_process on the audio thread. The
 * setters are lock-free and safe to call from the UI thread while audio runs — every value they touch
 * is parameter-smoothed inside the engine, so a change while playing can never click.
 */
#ifndef SOUNDSTAGE_ENGINE_C_H
#define SOUNDSTAGE_ENGINE_C_H

#if defined(_WIN32)
  #if defined(SOUNDSTAGE_ENGINE_EXPORTS)
    #define SSG_API __declspec(dllexport)
  #else
    #define SSG_API __declspec(dllimport)
  #endif
#elif defined(__GNUC__)
  #define SSG_API __attribute__((visibility("default")))
#else
  #define SSG_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct ssg_engine ssg_engine;

/* EQ band shapes — must match soundstage::Equalizer::BandType. */
enum {
  SSG_BAND_PEAKING    = 0,
  SSG_BAND_LOW_SHELF  = 1,
  SSG_BAND_HIGH_SHELF = 2,
  SSG_BAND_LOWPASS    = 3,
  SSG_BAND_HIGHPASS   = 4
};

/* ---- lifecycle ---- */
SSG_API ssg_engine* ssg_create(void);
SSG_API void        ssg_destroy(ssg_engine* e);
SSG_API void        ssg_prepare(ssg_engine* e, double sample_rate);
SSG_API void        ssg_reset(ssg_engine* e);   /* clear filter/delay state; keeps settings */

/* ---- audio (call on the audio thread) ----
 * Interleaved float32. `in` is `in_ch` channels (1 or 2); `out` is `out_ch` channels (2, 6, or 8).
 * Processes `frames` sample-frames. Safe with in==out aliasing only if out_ch<=in_ch. */
SSG_API void        ssg_process(ssg_engine* e, const float* in, int in_ch,
                                float* out, int out_ch, int frames);

/* For a source that is ALREADY multichannel (a real 5.1/7.1 stream rather than stereo we would have
 * to invent surrounds for). Channel order is the Windows one: FL FR FC LFE BL BR SL SR. The front
 * pair gets the whole chain; the centre and surrounds get EQ, master and their trim; the LFE gets
 * its trim only. */
SSG_API void        ssg_process_mc(ssg_engine* e, const float* in, int in_ch,
                                   float* out, int out_ch, int frames);

/* ---- master ---- */
SSG_API void        ssg_set_enabled(ssg_engine* e, int on);        /* clean bypass when off */
SSG_API void        ssg_set_output_gain_db(ssg_engine* e, double db);

/* ---- per-effect on/off (each ramps, pop-free) ---- */
SSG_API void        ssg_enable_eq(ssg_engine* e, int on);
SSG_API void        ssg_enable_bass(ssg_engine* e, int on);
SSG_API void        ssg_enable_compressor(ssg_engine* e, int on);
SSG_API void        ssg_enable_width(ssg_engine* e, int on);
SSG_API void        ssg_enable_reverb(ssg_engine* e, int on);
SSG_API void        ssg_enable_upmix(ssg_engine* e, int on);

/* ---- effect parameters ---- */
SSG_API void        ssg_eq_set_num_bands(ssg_engine* e, int n);
SSG_API void        ssg_eq_set_band(ssg_engine* e, int index, int type,
                                    double freq, double gain_db, double q);
SSG_API void        ssg_bass_set(ssg_engine* e, double amount, double crossover_hz, double drive);
SSG_API void        ssg_compressor_set(ssg_engine* e, double threshold_db, double ratio,
                                       double knee_db, double makeup_db,
                                       double attack_ms, double release_ms);
SSG_API void        ssg_width_set(ssg_engine* e, double width);    /* 0..2, 1 = unchanged */
SSG_API void        ssg_reverb_set(ssg_engine* e, double size, double decay_s, double damping,
                                   double predelay_ms, double width, double mix);
/* The rest of the Ambience page: input diffusion 0..1, and the send/tail band limits in Hz. */
SSG_API void        ssg_reverb_set_tone(ssg_engine* e, double diffusion,
                                        double low_cut_hz, double high_cut_hz);
/* Early reflections 0..1 (the discrete first bounces) and modulation 0..1 (slow detune of the
 * delay lines, which stops a sustained note ringing metallically). */
SSG_API void        ssg_reverb_set_character(ssg_engine* e, double early, double modulation);
SSG_API void        ssg_upmix_set(ssg_engine* e, double amount, double center_gain, double lfe_gain);
/* Feed the subwoofer from the stereo low end even with the upmix off. */
SSG_API void        ssg_enable_sub_feed(ssg_engine* e, int on);

/* Bass management — a receiver's "Speaker Size: Small". Removes everything below `crossover_hz` from
 * each speaker whose bit is set in `small_mask` (bit 0 = FL ... bit 7 = SR; the LFE bit is ignored)
 * and redirects it to the subwoofer. Copying bass to the sub without taking it off the speaker is
 * what makes a small satellite sound boomy, so this does both halves. */
SSG_API void        ssg_bass_management(ssg_engine* e, int on, double crossover_hz,
                                        int small_mask, double sub_gain);

/* Night mode — its own dynamics stage, separate from the Leveler, so you can run both. The point is
 * to stop sudden loud moments carrying through the house; the bass cut that goes with it is an EQ
 * shelf the host sets. */
SSG_API void        ssg_enable_night(ssg_engine* e, int on);
SSG_API void        ssg_night_set(ssg_engine* e, double threshold_db, double ratio,
                                  double makeup_db, double attack_ms, double release_ms);

/* Per-speaker output trim in dB — the calibration faders. `ch` is 0..7 in 7.1 order:
 * FL FR C LFE SL SR SBL SBR. Applied after the upmix, so it trims the real speaker. */
SSG_API void        ssg_set_channel_trim_db(ssg_engine* e, int ch, double db);

/* ---- meters (for the UI) ---- */
SSG_API double      ssg_meter_reduction_db(ssg_engine* e);

/* ABI/build version, so the app can sanity-check the DLL it loaded. */
SSG_API int         ssg_abi_version(void);
#define SSG_ABI_VERSION 3

#ifdef __cplusplus
}
#endif
#endif /* SOUNDSTAGE_ENGINE_C_H */
