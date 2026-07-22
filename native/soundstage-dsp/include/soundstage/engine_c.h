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
SSG_API void        ssg_upmix_set(ssg_engine* e, double amount, double center_gain, double lfe_gain);

/* ---- meters (for the UI) ---- */
SSG_API double      ssg_meter_reduction_db(ssg_engine* e);

/* ABI/build version, so the app can sanity-check the DLL it loaded. */
SSG_API int         ssg_abi_version(void);
#define SSG_ABI_VERSION 1

#ifdef __cplusplus
}
#endif
#endif /* SOUNDSTAGE_ENGINE_C_H */
