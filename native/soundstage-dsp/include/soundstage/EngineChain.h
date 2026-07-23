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
#include "soundstage/Limiter.h"
#include "soundstage/BassManager.h"
#include "soundstage/SmoothedValue.h"

#include <cmath>

namespace soundstage {

class EngineChain {
public:
    /// The widest layout we render (7.1: FL FR C LFE SL SR SBL SBR).
    static constexpr int kOutChannels = 8;

    void prepare(double sampleRate) {
        fs_ = sampleRate;
        eq_.prepare(sampleRate);
        eqCenter_.prepare(sampleRate);
        eqBack_.prepare(sampleRate);
        eqSide_.prepare(sampleRate);
        bass_.prepare(sampleRate);
        comp_.prepare(sampleRate);
        nightComp_.prepare(sampleRate);
        reverb_.prepare(sampleRate);
        upmix_.prepare(sampleRate, Upmix::Surround7_1);  // prepare the widest layout; 5.1 is its first 6 ch
        subLp_.setLowpass(sampleRate, 120.0, 0.707);
        subLp_.reset();
        bass2_.prepare(sampleRate);
        limiter_.prepare(sampleRate);

        // 20 ms enable ramps: inaudible, and long enough that nothing clicks. Effects start OFF, so
        // the chain is transparent out of the box (master on, every effect bypassed -> output = input).
        //
        // Every value here is re-seeded from the state the host has already set, NOT from a constant.
        // prepare() runs when audio starts and on every device or sample-rate change; if it reset
        // these it would silently switch off whatever the user had turned on — which is exactly the
        // bug where the EQ section died the moment the audio path opened.
        const double enableRamp = 0.02;
        eqEnable_.reset(sampleRate, enableRamp);      eqEnable_.setCurrentAndTarget(eqOn_ ? 1.0 : 0.0);
        eqEnableExtra_.reset(sampleRate, enableRamp); eqEnableExtra_.setCurrentAndTarget(eqOn_ ? 1.0 : 0.0);
        bassEnable_.reset(sampleRate, enableRamp);    bassEnable_.setCurrentAndTarget(bassOn_ ? 1.0 : 0.0);
        compEnable_.reset(sampleRate, enableRamp);    compEnable_.setCurrentAndTarget(compOn_ ? 1.0 : 0.0);
        nightEnable_.reset(sampleRate, enableRamp);   nightEnable_.setCurrentAndTarget(nightOn_ ? 1.0 : 0.0);
        widthEnable_.reset(sampleRate, enableRamp);   widthEnable_.setCurrentAndTarget(widthOn_ ? 1.0 : 0.0);
        reverbEnable_.reset(sampleRate, enableRamp);  reverbEnable_.setCurrentAndTarget(reverbOn_ ? 1.0 : 0.0);

        masterEnable_.reset(sampleRate, enableRamp);  masterEnable_.setCurrentAndTarget(masterOn_ ? 1.0 : 0.0);
        masterGain_.reset(sampleRate, enableRamp);    masterGain_.setCurrentAndTarget(masterGainLin_);
        upmixAmount_.reset(sampleRate, 0.05);         upmixAmount_.setCurrentAndTarget(upmixAmountValue_);

        // Per-speaker trims (the calibration faders). Seeded from the dB values the host already set,
        // so preparing for a new sample rate keeps the user's calibration instead of zeroing it.
        for (int c = 0; c < kOutChannels; ++c) {
            channelTrim_[c].reset(sampleRate, enableRamp);
            channelTrim_[c].setCurrentAndTarget(dbToGain(channelTrimDb_[c]));
        }

        reset();
    }

    /// Clear all filter/delay state (e.g. on a device or sample-rate change). Does not change settings.
    void reset() {
        eq_.reset();
        eqCenter_.reset();
        eqBack_.reset();
        eqSide_.reset();
        bass_.reset();
        comp_.reset();
        nightComp_.reset();
        limiter_.reset();
        bass2_.reset();
        reverb_.reset();
    }

    // ---- master controls ----
    // Each setter remembers the value as well as ramping to it, so prepare() can restore it.
    void setEnabled(bool on)        { masterOn_ = on; masterEnable_.setTarget(on ? 1.0 : 0.0); }
    void setOutputGainDb(double db) { masterGainLin_ = std::pow(10.0, db / 20.0); masterGain_.setTarget(masterGainLin_); }

    // ---- per-effect on/off (each ramps, pop-free) ----
    void enableEq(bool on)         { eqOn_ = on;     eqEnable_.setTarget(on ? 1.0 : 0.0);
                                                     eqEnableExtra_.setTarget(on ? 1.0 : 0.0); }
    void enableBass(bool on)       { bassOn_ = on;   bassEnable_.setTarget(on ? 1.0 : 0.0); }
    void enableCompressor(bool on) { compOn_ = on;   compEnable_.setTarget(on ? 1.0 : 0.0); }
    void enableWidth(bool on)      { widthOn_ = on;  widthEnable_.setTarget(on ? 1.0 : 0.0); }
    void enableReverb(bool on)     { reverbOn_ = on; reverbEnable_.setTarget(on ? 1.0 : 0.0); }

    /// Night mode: its own compressor, separate from the Leveler's, because the two do different
    /// jobs and you may well want both. This one exists to stop sudden loud moments carrying through
    /// the house — the bass cut that goes with it is an EQ shelf the host sets.
    void enableNight(bool on)      { nightOn_ = on; nightEnable_.setTarget(on ? 1.0 : 0.0); }
    Compressor& nightCompressor()  { return nightComp_; }
    void enableUpmix(bool on)      { upmixOn_ = on; }  // structural (changes channel count), not crossfaded

    /// Feed the LFE from the stereo low end even with the upmix off, so a subwoofer still gets used
    /// on ordinary 2.0 material. Independent of the surround fill on purpose — plenty of people want
    /// their sub working without anything being invented for the rear speakers.
    void enableSubFeed(bool on)    { subFeed_ = on; }

    /// Bass management — "Speaker Size: Small" on a receiver. Takes the low end off the speakers that
    /// can't handle it and gives it to the sub, rather than merely copying it there.
    BassManager& bassManager()     { return bass2_; }

    /// The safety net at the end of the chain. Without it the only thing stopping an over is the
    /// hard clamp, and a clamped peak is audible as a buzz rather than as loudness.
    void enableLimiter(bool on)    { limiterOn_ = on; }
    bool limiterEnabled() const    { return limiterOn_; }
    Limiter& limiter()             { return limiter_; }

    void setUpmixAmount(double a)  { upmixAmountValue_ = a; upmixAmount_.setTarget(a); }

    // ---- what the host asked for (survives prepare) ----
    bool eqEnabled() const     { return eqOn_; }
    bool bassEnabled() const   { return bassOn_; }
    bool reverbEnabled() const { return reverbOn_; }

    /// Per-speaker output trim in dB (the calibration faders): channel `c` in 7.1 order
    /// FL FR C LFE SL SR SBL SBR. Applied last, after the upmix, so it trims the actual speaker.
    void setChannelTrimDb(int c, double db) {
        if (c < 0 || c >= kOutChannels) return;
        channelTrimDb_[c] = db;
        channelTrim_[c].setTarget(dbToGain(db));
    }

    double channelTrimDb(int c) const {
        return (c < 0 || c >= kOutChannels) ? 0.0 : channelTrimDb_[c];
    }

    // ---- EQ, applied to every speaker ----
    // The graphic EQ is a tone control for the whole system, so it has to hit the centre and the
    // surrounds too, not just the front pair. Each group keeps its own filter state (you can't share
    // biquad memory between channels) but they all take the same coefficients.
    void setEqNumBands(int n) {
        eq_.setNumBands(n);
        eqCenter_.setNumBands(n);
        eqBack_.setNumBands(n);
        eqSide_.setNumBands(n);
    }

    void setEqBand(int i, Equalizer::BandType type, double freq, double gainDb, double q) {
        eq_.setBand(i, type, freq, gainDb, q);
        eqCenter_.setBand(i, type, freq, gainDb, q);
        eqBack_.setBand(i, type, freq, gainDb, q);
        eqSide_.setBand(i, type, freq, gainDb, q);
    }

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
        { double pl = l, pr = r; nightComp_.process(pl, pr); const double g = nightEnable_.next(); l = mix(l, pl, g); r = mix(r, pr, g); }
        { double pl = l, pr = r; width_.process(pl, pr);  const double g = widthEnable_.next();  l = mix(l, pl, g); r = mix(r, pr, g); }
        { double pl = l, pr = r; reverb_.process(pl, pr); const double g = reverbEnable_.next(); l = mix(l, pl, g); r = mix(r, pr, g); }

        // Master switch crossfades the whole processed signal against the untouched input, then gain.
        const double me = masterEnable_.next();
        l = mix(origL, l, me);
        r = mix(origR, r, me);
        const double mg = masterGain_.next();
        l *= mg;
        r *= mg;

        // Last thing before the speakers: catch anything the chain pushed over full scale. Placed
        // after the master gain so it protects against the volume control too.
        if (limiterOn_) { limiter_.process(l, r); }

        upmix_.setAmount(upmixAmount_.next());  // keep the surround fill level smoothed too
        writeOut(l, r, out, outChannels);
    }

    /// Process a buffer that is ALREADY multichannel — a real 5.1/7.1 source, rather than stereo we
    /// have to invent surrounds for.
    ///
    /// The front pair goes through the whole chain. The centre and surrounds get the EQ, the master
    /// switch, the output gain and their speaker trim, but not the stereo-only effects: width and
    /// reverb are built on an L/R relationship that doesn't exist for a centre channel, and running
    /// them per-speaker would smear a mix someone already balanced. The LFE is deliberately left
    /// alone apart from its trim — EQ'ing a channel that is already nothing but bass just doubles
    /// whatever the graphic EQ is doing down there.
    void processBlockMulti(const float* in, int inChannels, float* out, int outChannels, int numFrames) {
        const int copyCh = inChannels < outChannels ? inChannels : outChannels;

        for (int n = 0; n < numFrames; ++n) {
            const float* src = in + (n * inChannels);
            float* dst = out + (n * outChannels);

            double l = src[0];
            double r = inChannels >= 2 ? src[1] : l;
            const double origL = l, origR = r;

            { double pl = l, pr = r; eq_.process(pl, pr);     const double g = eqEnable_.next();     l = mix(l, pl, g); r = mix(r, pr, g); }
            { double pl = l, pr = r; bass_.process(pl, pr);   const double g = bassEnable_.next();   l = mix(l, pl, g); r = mix(r, pr, g); }
            { double pl = l, pr = r; comp_.process(pl, pr);   const double g = compEnable_.next();   l = mix(l, pl, g); r = mix(r, pr, g); }
            { double pl = l, pr = r; nightComp_.process(pl, pr); const double g = nightEnable_.next(); l = mix(l, pl, g); r = mix(r, pr, g); }
            { double pl = l, pr = r; width_.process(pl, pr);  const double g = widthEnable_.next();  l = mix(l, pl, g); r = mix(r, pr, g); }
            { double pl = l, pr = r; reverb_.process(pl, pr); const double g = reverbEnable_.next(); l = mix(l, pl, g); r = mix(r, pr, g); }

            const double me = masterEnable_.next();
            l = mix(origL, l, me);
            r = mix(origR, r, me);
            const double mg = masterGain_.next();
            if (limiterOn_) { limiter_.process(l, r); }

            // The remaining channels: EQ (except the LFE), then the same master fade and gain.
            double c = inChannels > 2 ? src[2] : 0.0;
            double lfe = inChannels > 3 ? src[3] : 0.0;
            double bl = inChannels > 4 ? src[4] : 0.0;
            double br = inChannels > 5 ? src[5] : 0.0;
            double sl = inChannels > 6 ? src[6] : 0.0;
            double sr = inChannels > 7 ? src[7] : 0.0;

            const double origC = c, origBL = bl, origBR = br, origSL = sl, origSR = sr;
            {
                double dummy = 0.0;
                eqCenter_.process(c, dummy);
                eqBack_.process(bl, br);
                eqSide_.process(sl, sr);
            }
            const double eg = eqEnableExtra_.next();
            c  = mix(origC,  c,  eg);
            bl = mix(origBL, bl, eg);
            br = mix(origBR, br, eg);
            sl = mix(origSL, sl, eg);
            sr = mix(origSR, sr, eg);

            double frame[kOutChannels] = { l, r, c, lfe, bl, br, sl, sr };

            // Same bass management a real multichannel source gets — a 5.1 film's centre channel
            // needs its low end redirected just as much as an upmixed one.
            bass2_.process(frame, outChannels);

            for (int i = 0; i < kOutChannels; ++i) {
                const double t = channelTrim_[i].next();
                frame[i] = clampSafe(frame[i] * mg * t);
            }

            for (int i = 0; i < outChannels; ++i) {
                dst[i] = (i < copyCh || i < kOutChannels) ? static_cast<float>(frame[i]) : 0.0f;
            }
        }
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
        // Advance every trim once per frame, whatever the channel count, so they all ramp at the same
        // rate and a device swap can't leave one mid-walk.
        double trim[kOutChannels];
        for (int c = 0; c < kOutChannels; ++c) trim[c] = channelTrim_[c].next();

        if (outChannels <= 2) {
            out[0] = clampSafe(l * trim[0]);
            if (outChannels == 2) out[1] = clampSafe(r * trim[1]);
            return;
        }

        double frame[kOutChannels] = {0.0};
        if (upmixOn_) {
            upmix_.process(l, r, frame);  // fills the 7.1 order: FL FR C LFE BL BR SL SR
        } else {
            // No upmix on a surround device: front L/R carry the signal, the rest stay silent —
            // except the sub, which can still be fed if the user wants their subwoofer used on
            // ordinary stereo, independent of any surround fill.
            frame[0] = l;
            frame[1] = r;
            if (subFeed_) {
                frame[kLfeChannel] = subLp_.process(0.5 * (l + r));
            }
        }

        // Bass management last: it needs the finished per-speaker signal to know what to take away.
        bass2_.process(frame, outChannels);

        for (int i = 0; i < outChannels && i < kOutChannels; ++i) {
            out[i] = clampSafe(frame[i] * trim[i]);
        }
    }

    static inline double dbToGain(double db) { return std::pow(10.0, db / 20.0); }

    double fs_ = 48000.0;

    Equalizer    eq_;
    // Same coefficients, separate filter state, for the channels a real multichannel source carries.
    Equalizer    eqCenter_, eqBack_, eqSide_;
    BassEnhancer bass_;
    Compressor   comp_;
    Compressor   nightComp_;   // night mode's own dynamics, independent of the Leveler
    StereoWidth  width_;
    Reverb       reverb_;
    Upmix        upmix_;

    SmoothedValue eqEnable_, bassEnable_, compEnable_, widthEnable_, reverbEnable_;
    SmoothedValue eqEnableExtra_;   // the same EQ enable, advanced once per frame on the extra channels
    SmoothedValue nightEnable_;
    SmoothedValue masterEnable_, masterGain_, upmixAmount_;
    bool upmixOn_ = false;

    // The authoritative settings — what the host asked for, independent of where a ramp happens to
    // be. prepare() re-seeds every smoothed value from these.
    static constexpr int kLfeChannel = 3;
    Biquad subLp_;              // low-pass for the stereo-to-sub feed
    BassManager bass2_;         // "Speaker Size: Small" — takes bass off satellites, gives it to the sub
    Limiter limiter_;
    bool   limiterOn_ = false;
    bool   subFeed_ = false;
    bool   eqOn_ = false, bassOn_ = false, compOn_ = false, widthOn_ = false, reverbOn_ = false;
    bool   nightOn_ = false;
    bool   masterOn_ = true;
    double masterGainLin_ = 1.0;
    double upmixAmountValue_ = 0.7;

    // Speaker trims start at unity (0 dB) so an un-calibrated system is untouched.
    SmoothedValue channelTrim_[kOutChannels];
    double        channelTrimDb_[kOutChannels] = {0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0};
};

}  // namespace soundstage
