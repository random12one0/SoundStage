// Soundstage engine — C ABI implementation.
//
// A thin, allocation-free-on-the-audio-path wrapper that hands the flat C calls straight to the C++
// EngineChain. All the real work (and all the pop-free parameter smoothing) lives in EngineChain and
// the effects; this file only bridges the language boundary the app talks across.
#define SOUNDSTAGE_ENGINE_EXPORTS
#include "soundstage/engine_c.h"

#include "soundstage/EngineChain.h"

#include <new>

using soundstage::EngineChain;
using soundstage::Equalizer;

// The opaque handle the app holds is just an EngineChain.
struct ssg_engine {
    EngineChain chain;
};

extern "C" {

ssg_engine* ssg_create(void) { return new (std::nothrow) ssg_engine(); }
void        ssg_destroy(ssg_engine* e) { delete e; }
void        ssg_prepare(ssg_engine* e, double sample_rate) { if (e) e->chain.prepare(sample_rate); }
void        ssg_reset(ssg_engine* e) { if (e) e->chain.reset(); }

void ssg_process(ssg_engine* e, const float* in, int in_ch, float* out, int out_ch, int frames) {
    if (e && in && out && frames > 0) {
        e->chain.processBlock(in, in_ch, out, out_ch, frames);
    }
}

void ssg_process_mc(ssg_engine* e, const float* in, int in_ch, float* out, int out_ch, int frames) {
    if (e && in && out && frames > 0 && in_ch > 0 && out_ch > 0) {
        e->chain.processBlockMulti(in, in_ch, out, out_ch, frames);
    }
}

void ssg_set_enabled(ssg_engine* e, int on) { if (e) e->chain.setEnabled(on != 0); }
void ssg_set_output_gain_db(ssg_engine* e, double db) { if (e) e->chain.setOutputGainDb(db); }

void ssg_enable_eq(ssg_engine* e, int on)         { if (e) e->chain.enableEq(on != 0); }
void ssg_enable_bass(ssg_engine* e, int on)       { if (e) e->chain.enableBass(on != 0); }
void ssg_enable_compressor(ssg_engine* e, int on) { if (e) e->chain.enableCompressor(on != 0); }
void ssg_enable_width(ssg_engine* e, int on)      { if (e) e->chain.enableWidth(on != 0); }
void ssg_enable_reverb(ssg_engine* e, int on)     { if (e) e->chain.enableReverb(on != 0); }
void ssg_enable_upmix(ssg_engine* e, int on)      { if (e) e->chain.enableUpmix(on != 0); }

/* These fan out to every channel group's EQ — the graphic EQ is a system tone control, so it has to
 * reach the centre and surrounds, not only the front pair. */
void ssg_eq_set_num_bands(ssg_engine* e, int n) { if (e) e->chain.setEqNumBands(n); }

void ssg_eq_set_band(ssg_engine* e, int index, int type, double freq, double gain_db, double q) {
    if (!e) return;
    if (type < 0 || type > 4) type = 0;
    e->chain.setEqBand(index, static_cast<Equalizer::BandType>(type), freq, gain_db, q);
}

void ssg_bass_set(ssg_engine* e, double amount, double crossover_hz, double drive) {
    if (!e) return;
    e->chain.bass().setAmount(amount);
    e->chain.bass().setCrossover(crossover_hz);
    e->chain.bass().setDrive(drive);
}

void ssg_compressor_set(ssg_engine* e, double threshold_db, double ratio, double knee_db,
                        double makeup_db, double attack_ms, double release_ms) {
    if (!e) return;
    auto& c = e->chain.compressor();
    c.setThresholdDb(threshold_db);
    c.setRatio(ratio);
    c.setKneeDb(knee_db);
    c.setMakeupDb(makeup_db);
    c.setAttackMs(attack_ms);
    c.setReleaseMs(release_ms);
}

void ssg_width_set(ssg_engine* e, double width) { if (e) e->chain.width().setWidth(width); }

void ssg_reverb_set(ssg_engine* e, double size, double decay_s, double damping,
                    double predelay_ms, double width, double mix) {
    if (!e) return;
    auto& r = e->chain.reverb();
    r.setSize(size);
    r.setDecaySeconds(decay_s);
    r.setDamping(damping);
    r.setPreDelayMs(predelay_ms);
    r.setWidth(width);
    r.setMix(mix);
}

void ssg_reverb_set_tone(ssg_engine* e, double diffusion, double low_cut_hz, double high_cut_hz) {
    if (!e) return;
    auto& r = e->chain.reverb();
    r.setDiffusion(diffusion);
    r.setLowCutHz(low_cut_hz);
    r.setHighCutHz(high_cut_hz);
}

void ssg_enable_sub_feed(ssg_engine* e, int on) { if (e) e->chain.enableSubFeed(on != 0); }

void ssg_reverb_set_character(ssg_engine* e, double early, double modulation) {
    if (!e) return;
    e->chain.reverb().setEarlyLevel(early);
    e->chain.reverb().setModulation(modulation);
}

void ssg_upmix_set(ssg_engine* e, double amount, double center_gain, double lfe_gain) {
    if (!e) return;
    e->chain.setUpmixAmount(amount);
    e->chain.upmix().setCenterGain(center_gain);
    e->chain.upmix().setLfeGain(lfe_gain);
}

void ssg_set_channel_trim_db(ssg_engine* e, int ch, double db) {
    if (e) e->chain.setChannelTrimDb(ch, db);
}

double ssg_meter_reduction_db(ssg_engine* e) { return e ? e->chain.compressorReductionDb() : 0.0; }

int ssg_abi_version(void) { return SSG_ABI_VERSION; }

}  // extern "C"
