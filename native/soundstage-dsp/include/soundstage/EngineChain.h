// Soundstage DSP engine — the full effect chain.
//
// This is the single block the audio driver hands every buffer to. It owns all the effects and runs
// them in one fixed, musically-correct order:
//
//     EQ -> Bass -> Compressor(Leveler/Night) -> Stereo width -> Reverb -> Surround upmix -> output
//
// EQ first (tonal shaping on the clean signal); dynamics before the spatial effects (so the reverb
// tail is never pumped); the upmix last, because it's the only stage that changes the channel count
// (stereo in -> 2 / 6 / 8 out).
//
// How it can never pop: every effect has its OWN smoothed enable (0..1). The effect always runs, and
// its output is *crossfaded* against the signal that entered it using that smoothed value. Toggling an
// effect — or the master switch — ramps over ~20 ms instead of jumping, so there is no click, and a
// re-enabled effect is already warmed up (its state kept running while "off"). The same applies to the
// master enable (the big Off switch) and the output gain.
//
// Portable, double-precision C++17: unit-tested here on Linux, and the identical code runs inside the
// Windows engine that hosts it.
#pragma once

#include "soundstage/Equalizer.h"
#include "soundstage/BassEnhancer.h"
#include "soundstage/Compressor.h"
#include "soundstage/StereoWidth.h"
#include "soundstage/Reverb.h"
#include "soundstage/Upmix.h"
#include "soundstage/SmoothedValue.h"

#include <cmath>

namespace soundstage {

class EngineChain {
public:
    void prepare(double sampleRate) {
        fs_ = sampleRate;
        eq_.prepare(sampleRate);
        bass_.prepare(sampleRate);
        comp_.prepare(sampleRate);
        reverb_.prepare(sampleRate);
        upmix_.prepare(sampleRate, Upmix::Surround7_1);  // prepare the widest layout; 5.1 is its first 6 ch

        // 20 ms enable ramps: inaudible, and long enough that nothing clicks. All effects start OFF, so
        // the chain is transparent out of the box (master on, every effect bypassed -> output = input).
        const double enableRamp = 0.02;
        eqEnable_.reset(sampleRate, enableRamp);      eqEnable_.setCurrentAndTarget(0.0);
        bassEnable_.reset(sampleRate, enableRamp);    bassEnable_.setCurrentAndTarget(0.0);
        compEnable_.reset(sampleRate, enableRamp);    compEnable_.setCurrentAndTarget(0.0);
        widthEnable_.reset(sampleRate, enableRamp);   widthEnable_.setCurrentAndTarget(0.0);
        reverbEnable_.reset(sampleRate, enableRamp);  reverbEnable_.setCurrentAndTarget(0.0);

        masterEnable_.reset(sampleRate, enableRamp);  masterEnable_.setCurrentAndTarget(1.0);
        masterGain_.reset(sampleRate, enableRamp);    masterGain_.setCurrentAndTarget(1.0);
        upmixAmount_.reset(sampleRate, 0.05);         upmixAmount_.setCurrentAndTarget(0.7);

        reset();
    }

    /// Clear all filter/delay state (e.g. on a device or sample-rate change). Does not change settings.
    void reset() {
        eq_.reset();
        bass_.reset();
        comp_.reset();
        reverb_.reset();
    }

    // ---- master controls ----
    void setEnabled(bool on)        { masterEnable_.setTarget(on ? 1.0 : 0.0); }
    void setOutputGainDb(double db) { masterGain_.setTarget(std::pow(10.0, db / 20.0)); }

    // ---- per-effect on/off (each ramps, pop-free) ----
    void enableEq(bool on)         { eqEnable_.setTarget(on ? 1.0 : 0.0); }
    void enableBass(bool on)       { bassEnable_.setTarget(on ? 1.0 : 0.0); }
    void enableCompressor(bool on) { compEnable_.setTarget(on ? 1.0 : 0.0); }
    void enableWidth(bool on)      { widthEnable_.setTarget(on ? 1.0 : 0.0); }
    void enableReverb(bool on)     { reverbEnable_.setTarget(on ? 1.0 : 0.0); }
    void enableUpmix(bool on)      { upmixOn_ = on; }  // structural (changes channel count), not crossfaded

    void setUpmixAmount(double a)  { upmixAmount_.setTarget(a); }

    // ---- direct access to configure each effect (the host drives these) ----
    Equalizer&    eq()         { return eq_; }
    BassEnhancer& bass()       { return bass_; }
    Compressor&   compressor() { return comp_; }
    StereoWidth&  width()      { return width_; }
    Reverb&       reverb()     { return reverb_; }
    Upmix&        upmix()      { return upmix_; }

    // ---- meters (for the UI) ----
    double compressorReductionDb() const { return comp_.gainReductionDb(); }

    /// Process one stereo frame. Writes `outChannels` interleaved samples (2, 6, or 8) into `out`.
    inline void processFrame(double inL, double inR, double* out, int outChannels) {
        const double origL = inL, origR = inR;
        double l = inL, r = inR;

        // Each stage: run the effect, then crossfade wet<-dry by its smoothed enable.
        { double pl = l, pr = r; eq_.process(pl, pr);     const double g = eqEnable_.next();     l = mix(l, pl, g); r = mix(r, pr, g); }
        { double pl = l, pr = r; bass_.process(pl, pr);   const double g = bassEnable_.next();   l = mix(l, pl, g); r = mix(r, pr, g); }
        { double pl = l, pr = r; comp_.process(pl, pr);   const double g = compEnable_.next();   l = mix(l, pl, g); r = mix(r, pr, g); }
        { double pl = l, pr = r; width_.process(pl, pr);  const double g = widthEnable_.next();  l = mix(l, pl, g); r = mix(r, pr, g); }
        { double pl = l, pr = r; reverb_.process(pl, pr); const double g = reverbEnable_.next(); l = mix(l, pl, g); r = mix(r, pr, g); }

        // Master switch crossfades the whole processed signal against the untouched input, then gain.
        const double me = masterEnable_.next();
        l = mix(origL, l, me);
        r = mix(origR, r, me);
        const double mg = masterGain_.next();
        l *= mg;
        r *= mg;

        upmix_.setAmount(upmixAmount_.next());  // keep the surround fill level smoothed too
        writeOut(l, r, out, outChannels);
    }

    /// Process an interleaved buffer — what the driver calls. `in` is `inChannels` interleaved
    /// (mono or stereo); `out` is `outChannels` interleaved. Everything is processed in double and
    /// written back as float.
    void processBlock(const float* in, int inChannels, float* out, int outChannels, int numFrames) {
        for (int n = 0; n < numFrames; ++n) {
            double l, r;
            if (inChannels >= 2) {
                l = static_cast<double>(in[n * inChannels]);
                r = static_cast<double>(in[n * inChannels + 1]);
            } else {
                l = r = static_cast<double>(in[n * inChannels]);
            }
            double frame[8];
            processFrame(l, r, frame, outChannels);
            for (int c = 0; c < outChannels; ++c) {
                out[n * outChannels + c] = static_cast<float>(frame[c]);
            }
        }
    }

private:
    static inline double mix(double dry, double wet, double g) { return dry + (wet - dry) * g; }

    // Final safety clamp: keeps output inside [-1, 1] so a stray over can never wrap on int conversion.
    // (A true brickwall limiter comes later; the compressor is the musical dynamics stage.)
    static inline double clampSafe(double x) { return x < -1.0 ? -1.0 : (x > 1.0 ? 1.0 : x); }

    inline void writeOut(double l, double r, double* out, int outChannels) {
        if (outChannels <= 2) {
            out[0] = clampSafe(l);
            if (outChannels == 2) out[1] = clampSafe(r);
            return;
        }
        if (upmixOn_) {
            double up[8] = {0.0};
            upmix_.process(l, r, up);  // fills the 7.1 order: FL FR C LFE SL SR SBL SBR
            for (int i = 0; i < outChannels && i < 8; ++i) out[i] = clampSafe(up[i]);
        } else {
            // No upmix on a surround device: front L/R carry the signal, the rest stay silent.
            for (int i = 0; i < outChannels; ++i) out[i] = 0.0;
            out[0] = clampSafe(l);
            out[1] = clampSafe(r);
        }
    }

    double fs_ = 48000.0;

    Equalizer    eq_;
    BassEnhancer bass_;
    Compressor   comp_;
    StereoWidth  width_;
    Reverb       reverb_;
    Upmix        upmix_;

    SmoothedValue eqEnable_, bassEnable_, compEnable_, widthEnable_, reverbEnable_;
    SmoothedValue masterEnable_, masterGain_, upmixAmount_;
    bool upmixOn_ = false;
};

}  // namespace soundstage
