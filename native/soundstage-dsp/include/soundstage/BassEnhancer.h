// Soundstage DSP engine — psychoacoustic bass enhancement ("Virtual Bass").
//
// Small speakers can't move enough air for deep bass, so we use the "missing fundamental" illusion:
// isolate the low band, synthesise its harmonics (which small drivers CAN reproduce), high-pass those
// so we don't ask the speaker for sub it can't make, and mix them back. The ear reconstructs the
// deep note from the harmonic series. An optional low shelf adds real sub for rigs that have one.
#pragma once

#include "soundstage/Biquad.h"

#include <algorithm>
#include <cmath>

namespace soundstage {

class BassEnhancer {
public:
    void prepare(double sampleRate) {
        fs_ = sampleRate;
        updateFilters();
        reset();
    }
    void reset() {
        for (auto* b : {&bandL_, &bandR_, &hpL_, &hpR_}) b->reset();
    }

    void setAmount(double a)       { amount_ = clamp(a, 0.0, 1.0); }
    void setCrossover(double hz)   { crossover_ = clamp(hz, 60.0, 200.0); updateFilters(); }
    void setDrive(double d)        { drive_ = clamp(d, 0.5, 6.0); }

    inline void process(double& l, double& r) {
        l = one(l, bandL_, hpL_);
        r = one(r, bandR_, hpR_);
    }

private:
    static double clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

    void updateFilters() {
        // Isolate the low band, and high-pass the synthesised harmonics back above the crossover so
        // we never feed the speaker sub it can't reproduce.
        bandL_.setLowpass(fs_, crossover_, 0.707);
        bandR_.setLowpass(fs_, crossover_, 0.707);
        hpL_.setHighpass(fs_, crossover_, 0.707);
        hpR_.setHighpass(fs_, crossover_, 0.707);
    }

    inline double one(double x, Biquad& band, Biquad& hp) {
        const double low = band.process(x);
        // Odd-harmonic generator (tanh) minus the linear part → keeps only the new harmonics.
        const double shaped = std::tanh(drive_ * low) - drive_ * low * 0.4;
        const double harmonics = hp.process(shaped);
        return x + amount_ * harmonics;
    }

    double fs_ = 48000.0;
    double amount_ = 0.5, crossover_ = 110.0, drive_ = 2.0;
    Biquad bandL_, bandR_, hpL_, hpR_;
};

}  // namespace soundstage
