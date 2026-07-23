// Soundstage DSP engine — the safety net at the end of the chain.
//
// Every stage before this can add level: a bass shelf, a compressor's makeup, an EQ boost. Push
// enough of them and the signal asks for more than a sample can represent. Without a limiter the
// only thing left is the hard clamp in EngineChain, and a clamped waveform has its peaks sliced
// flat — which is not "loud", it's a buzz. That is the specific noise this exists to prevent.
//
// The design is a lookahead peak limiter, which is the honest way to do it:
//
//   - We delay the audio by a few milliseconds and look at what is *about* to arrive.
//   - If a peak in that window would exceed the ceiling, the gain is already on its way down by the
//     time the peak actually plays. No overshoot, and no distortion from reacting late.
//   - Gain recovers slowly afterwards, so a single transient doesn't audibly duck everything around
//     it, and repeated transients don't make the level pump.
//
// The gain envelope is smoothed with a one-pole release rather than snapped, so what you hear is the
// loud moment being held back rather than the limiter working.
#pragma once

#include <algorithm>
#include <cmath>
#include <vector>

namespace soundstage {

class Limiter {
public:
    void prepare(double sampleRate) {
        fs_ = sampleRate;

        // 3 ms of lookahead: long enough to catch a transient's rise, short enough that the extra
        // latency is inaudible against picture (well under a single video frame).
        look_ = static_cast<std::size_t>(0.003 * fs_);
        if (look_ < 1) look_ = 1;

        bufL_.assign(look_ + 1, 0.0);
        bufR_.assign(look_ + 1, 0.0);

        // The peak-hold window matches the lookahead, so a peak stays "seen" for exactly as long as
        // it takes to reach the output.
        hold_.assign(look_ + 1, 0.0);

        setRelease(releaseMs_);
        reset();
    }

    void reset() {
        std::fill(bufL_.begin(), bufL_.end(), 0.0);
        std::fill(bufR_.begin(), bufR_.end(), 0.0);
        std::fill(hold_.begin(), hold_.end(), 0.0);
        pos_ = 0;
        gain_ = 1.0;
        reduction_ = 0.0;
    }

    /// Output ceiling in dBFS. Slightly below 0 by default: converters and any downstream resampling
    /// can overshoot a signal that sits exactly at full scale.
    void setCeilingDb(double db) { ceiling_ = std::pow(10.0, std::min(db, 0.0) / 20.0); }

    /// How quickly gain comes back after a peak. Too fast pumps; too slow ducks the following music.
    void setRelease(double ms) {
        releaseMs_ = std::max(1.0, ms);
        relCoeff_ = std::exp(-1.0 / (releaseMs_ * 0.001 * fs_));
    }

    /// How much the limiter is pulling back right now, in dB — for the UI meter.
    double reductionDb() const { return reduction_; }

    inline void process(double& l, double& r) {
        // Remember the incoming sample, and take the one that is `look_` samples old.
        const double delayedL = bufL_[pos_];
        const double delayedR = bufR_[pos_];
        bufL_[pos_] = l;
        bufR_[pos_] = r;

        // Peak of the pair, so the stereo image never shifts: both channels always share one gain.
        hold_[pos_] = std::max(std::fabs(l), std::fabs(r));

        if (++pos_ >= bufL_.size()) pos_ = 0;

        // The loudest thing anywhere in the lookahead window.
        double peak = 0.0;
        for (double v : hold_) peak = std::max(peak, v);

        // The gain that peak would need. 1.0 when nothing is too loud.
        const double target = peak > ceiling_ ? (ceiling_ / peak) : 1.0;

        // Attack is instant — we already know the peak is coming, so there is no reason to be late.
        // Release is gradual.
        gain_ = target < gain_ ? target : (target + (gain_ - target) * relCoeff_);

        reduction_ = gain_ < 1.0 ? -20.0 * std::log10(gain_) : 0.0;

        l = delayedL * gain_;
        r = delayedR * gain_;
    }

private:
    double fs_ = 48000.0;
    std::size_t look_ = 1, pos_ = 0;
    std::vector<double> bufL_, bufR_, hold_;
    double ceiling_ = 0.891;      // -1 dBFS
    double gain_ = 1.0, reduction_ = 0.0;
    double releaseMs_ = 80.0, relCoeff_ = 0.0;
};

}  // namespace soundstage
