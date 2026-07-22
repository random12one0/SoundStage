// Soundstage DSP engine — our own stereo → 5.1 / 7.1 upmix.
//
// Deliberately simple and surround-speaker-first (the product thesis): front L/R pass straight
// through, a derived Centre carries the correlated (mono) content, the LFE gets a low-passed sum,
// and the surround/back speakers are filled from the fronts at a chosen level. No fancy steering,
// no phase tricks that collapse the image — just fills your speakers cleanly. Off by default in the
// app; Atmos stays in charge when it's active.
#pragma once

#include "soundstage/Biquad.h"

#include <algorithm>

namespace soundstage {

class Upmix {
public:
    enum Layout { Surround5_1 = 6, Surround7_1 = 8 };

    void prepare(double sampleRate, Layout layout) {
        layout_ = layout;
        lfeLp_.setLowpass(sampleRate, 120.0, 0.707);  // sub content only
        lfeLp_.reset();
    }

    void setAmount(double a)     { amount_ = clamp01(a); }       // how much surround/back fill
    void setCenterGain(double g) { center_ = std::max(0.0, g); }
    void setLfeGain(double g)    { lfe_ = std::max(0.0, g); }

    int channels() const { return static_cast<int>(layout_); }

    /// Expand one stereo frame into `channels()` output samples.
    /// Order: FL, FR, C, LFE, SL, SR [, SBL, SBR].
    inline void process(double l, double r, double* out) {
        const double mid = 0.5 * (l + r);
        const double surr = amount_ * 0.85;
        out[0] = l;                       // Front L
        out[1] = r;                       // Front R
        out[2] = mid * center_;           // Centre — correlated content
        out[3] = lfeLp_.process(mid) * lfe_;  // LFE — low-passed sum
        out[4] = l * surr;                // Side/Surround L
        out[5] = r * surr;                // Side/Surround R
        if (layout_ == Surround7_1) {
            out[6] = l * surr;            // Back L
            out[7] = r * surr;            // Back R
        }
    }

private:
    static double clamp01(double v) { return v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v); }
    Layout layout_ = Surround7_1;
    Biquad lfeLp_;
    double amount_ = 0.7, center_ = 1.0, lfe_ = 1.0;
};

}  // namespace soundstage
