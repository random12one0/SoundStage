// Soundstage DSP engine — dynamics: a feed-forward compressor / limiter.
//
// This is the "Leveler" effect and the guts of Night mode. Standard log-domain design: a peak
// detector with separate attack/release, a gain computer with a soft knee, and makeup gain. Set a
// high ratio + fast attack + low threshold and it behaves as a brickwall limiter (speaker/clip
// protection). Stereo detection is linked (max of both channels) so the stereo image is preserved.
#pragma once

#include <algorithm>
#include <cmath>

namespace soundstage {

class Compressor {
public:
    void prepare(double sampleRate) { fs_ = sampleRate; setAttackMs(attackMs_); setReleaseMs(releaseMs_); reset(); }
    void reset() { env_ = 0.0; }

    void setThresholdDb(double db) { thresholdDb_ = db; }
    void setRatio(double r)        { ratio_ = std::max(1.0, r); }
    void setKneeDb(double db)      { kneeDb_ = std::max(0.0, db); }
    void setMakeupDb(double db)    { makeup_ = std::pow(10.0, db / 20.0); }
    void setAttackMs(double ms)    { attackMs_ = ms; aAtt_ = coeff(ms); }
    void setReleaseMs(double ms)   { releaseMs_ = ms; aRel_ = coeff(ms); }

    /// Process one stereo sample in place.
    inline void process(double& l, double& r) {
        const double key = std::max(std::fabs(l), std::fabs(r));
        // Peak envelope follower (attack when rising, release when falling).
        const double a = key > env_ ? aAtt_ : aRel_;
        env_ = a * env_ + (1.0 - a) * key;

        const double gain = computeGain(env_) * makeup_;
        l *= gain;
        r *= gain;
    }

    /// Current gain reduction in dB (>= 0), for a meter.
    double gainReductionDb() const { return -20.0 * std::log10(std::max(1e-9, computeGain(env_))); }

private:
    double coeff(double ms) const {
        if (ms <= 0.0) return 0.0;
        return std::exp(-1.0 / (0.001 * ms * fs_));
    }

    // Log-domain gain computer with a soft knee. Returns a linear gain in (0,1].
    double computeGain(double env) const {
        const double envDb = 20.0 * std::log10(std::max(1e-9, env));
        const double over = envDb - thresholdDb_;
        double reductionDb;
        if (kneeDb_ > 0.0 && over > -kneeDb_ * 0.5 && over < kneeDb_ * 0.5) {
            // Quadratic soft knee.
            const double x = over + kneeDb_ * 0.5;
            reductionDb = (1.0 / ratio_ - 1.0) * (x * x) / (2.0 * kneeDb_);
        } else if (over >= kneeDb_ * 0.5) {
            reductionDb = (1.0 / ratio_ - 1.0) * over;
        } else {
            reductionDb = 0.0;
        }
        return std::pow(10.0, reductionDb / 20.0);
    }

    double fs_ = 48000.0;
    double thresholdDb_ = -18.0, ratio_ = 3.0, kneeDb_ = 6.0, makeup_ = 1.0;
    double attackMs_ = 10.0, releaseMs_ = 120.0;
    double aAtt_ = 0.0, aRel_ = 0.0, env_ = 0.0;
};

}  // namespace soundstage
