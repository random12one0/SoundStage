// Soundstage DSP engine — a stereo multi-band equalizer.
//
// A cascade of biquad bands applied identically to L and R. The host configures each band as a
// peaking bell, a shelf, or a pass filter — so the same class backs both the parametric EQ and the
// 31-band graphic EQ in the app. Bands beyond the active count are skipped; a configured-but-flat
// band is a true identity (unity gain), so "reset to flat" costs nothing and never colours the sound.
// Double precision throughout, matching the rest of the engine.
#pragma once

#include "soundstage/Biquad.h"

#include <array>

namespace soundstage {

class Equalizer {
public:
    static constexpr int kMaxBands = 32;

    enum class BandType { Peaking, LowShelf, HighShelf, Lowpass, Highpass };

    void prepare(double sampleRate) {
        fs_ = sampleRate;
        reset();
    }

    void reset() {
        for (auto& b : bandsL_) b.reset();
        for (auto& b : bandsR_) b.reset();
    }

    /// How many bands are live (processed in series). Bands above this are ignored.
    void setNumBands(int n) { numBands_ = clampCount(n); }
    int numBands() const { return numBands_; }

    /// Configure band `i`. Left and right get identical coefficients so the stereo image is untouched.
    void setBand(int i, BandType type, double freq, double gainDb, double q) {
        if (i < 0 || i >= kMaxBands) return;
        configure(bandsL_[i], type, freq, gainDb, q);
        configure(bandsR_[i], type, freq, gainDb, q);
    }

    /// Make band `i` a unity identity (flat) — used to clear a band without changing the band count.
    void setBandFlat(int i) {
        if (i < 0 || i >= kMaxBands) return;
        bandsL_[i].setPeaking(fs_, 1000.0, 0.0, 1.0);
        bandsR_[i].setPeaking(fs_, 1000.0, 0.0, 1.0);
    }

    /// Process one stereo sample in place, through every active band in series.
    inline void process(double& l, double& r) {
        for (int i = 0; i < numBands_; ++i) {
            l = bandsL_[i].process(l);
            r = bandsR_[i].process(r);
        }
    }

    /// Combined linear magnitude at `f` Hz across all active bands — for drawing the UI response curve.
    double magnitude(double f) const {
        double m = 1.0;
        for (int i = 0; i < numBands_; ++i) m *= bandsL_[i].magnitude(fs_, f);
        return m;
    }

private:
    static int clampCount(int n) { return n < 0 ? 0 : (n > kMaxBands ? kMaxBands : n); }

    void configure(Biquad& b, BandType type, double freq, double gainDb, double q) {
        switch (type) {
            case BandType::Peaking:   b.setPeaking(fs_, freq, gainDb, q);  break;
            case BandType::LowShelf:  b.setLowShelf(fs_, freq, gainDb, q); break;
            case BandType::HighShelf: b.setHighShelf(fs_, freq, gainDb, q);break;
            case BandType::Lowpass:   b.setLowpass(fs_, freq, q);          break;
            case BandType::Highpass:  b.setHighpass(fs_, freq, q);         break;
        }
    }

    double fs_ = 48000.0;
    int numBands_ = 0;
    std::array<Biquad, kMaxBands> bandsL_{};
    std::array<Biquad, kMaxBands> bandsR_{};
};

}  // namespace soundstage
