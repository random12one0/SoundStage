// Soundstage DSP engine — bass management, the way a receiver does it.
//
// This is the "Speaker Size: Large / Small" setting every AV receiver has, and it is two operations,
// not one:
//
//   1. TAKE the low end away from a speaker that can't reproduce it (high-pass it), and
//   2. GIVE that low end to the subwoofer instead (sum it, low-pass it, add it to the LFE).
//
// Doing only the second half — which is what a naive "send bass to the sub" switch does — leaves the
// bass playing out of BOTH the small speaker and the sub. You get a doubled, boomy low end, the small
// driver still struggles with content it was never going to handle, and the crossover region gets a
// hump where the two overlap. Removing it from the satellite is the point.
//
// The filters are Linkwitz-Riley 4th order (two cascaded Butterworth sections). That pairing is used
// because an LR4 high-pass and low-pass at the same frequency sum back to flat — so a speaker marked
// Small plus the sub reproduces the same response the speaker would have had if it could reach that
// low. A single Butterworth pair does not sum flat; it leaves a +3 dB bump at the crossover.
//
// 80 Hz is the default because it is the long-standing cinema convention: low enough that almost no
// directional information is lost to the sub, high enough that a typical satellite is off the hook.
#pragma once

#include "soundstage/Biquad.h"

namespace soundstage {

class BassManager {
public:
    static constexpr int kChannels = 8;
    static constexpr int kLfe = 3;

    void prepare(double sampleRate) {
        fs_ = sampleRate;
        updateFilters();
        reset();
    }

    void reset() {
        for (int c = 0; c < kChannels; ++c) {
            hp1_[c].reset(); hp2_[c].reset();
            lp1_[c].reset(); lp2_[c].reset();
        }
        subLp1_.reset();
        subLp2_.reset();
    }

    void setEnabled(bool on) { enabled_ = on; }
    bool enabled() const { return enabled_; }

    /// Crossover point in Hz. Everything below this leaves a "Small" speaker and goes to the sub.
    void setCrossover(double hz) {
        crossover_ = hz < 40.0 ? 40.0 : (hz > 200.0 ? 200.0 : hz);
        updateFilters();
    }

    double crossover() const { return crossover_; }

    /// Mark a speaker Small (true) or Large (true = it needs help; false = it can handle its own bass).
    void setSmall(int channel, bool small) {
        if (channel < 0 || channel >= kChannels) return;
        small_[channel] = small;
    }

    bool isSmall(int channel) const {
        return channel >= 0 && channel < kChannels && small_[channel];
    }

    /// How much of the redirected bass reaches the sub. 1.0 is unity; receivers often run a little
    /// hot here, but that's a taste decision the user makes with the LFE trim.
    void setSubGain(double g) { subGain_ = g < 0.0 ? 0.0 : g; }

    /// Redistribute the low end across one frame, in place. `channels` is the real output width, so
    /// this does nothing useful (and correctly does nothing) on a stereo device with no sub.
    inline void process(double* frame, int channels) {
        if (!enabled_ || channels <= kLfe) {
            return;   // no LFE channel to send anything to — leave the signal alone
        }

        double redirected = 0.0;

        for (int c = 0; c < channels && c < kChannels; ++c) {
            if (c == kLfe || !small_[c]) {
                continue;   // the sub itself, and anything Large, keeps what it has
            }

            const double in = frame[c];

            // Low half goes to the sub; high half stays on the speaker. Both are LR4 so the two
            // halves add back up to the original.
            const double low = lp2_[c].process(lp1_[c].process(in));
            const double high = hp2_[c].process(hp1_[c].process(in));

            frame[c] = high;
            redirected += low;
        }

        // One more low-pass on the summed result, so nothing above the crossover can leak into the
        // sub through the summation itself.
        frame[kLfe] += subLp2_.process(subLp1_.process(redirected)) * subGain_;
    }

private:
    void updateFilters() {
        for (int c = 0; c < kChannels; ++c) {
            hp1_[c].setHighpass(fs_, crossover_, kButterworthQ);
            hp2_[c].setHighpass(fs_, crossover_, kButterworthQ);
            lp1_[c].setLowpass(fs_, crossover_, kButterworthQ);
            lp2_[c].setLowpass(fs_, crossover_, kButterworthQ);
        }
        subLp1_.setLowpass(fs_, crossover_, kButterworthQ);
        subLp2_.setLowpass(fs_, crossover_, kButterworthQ);
    }

    // Two cascaded sections at this Q give Linkwitz-Riley 4th order, which is the pairing that sums
    // back to flat through the crossover region.
    static constexpr double kButterworthQ = 0.7071067811865476;

    double fs_ = 48000.0;
    double crossover_ = 80.0;
    double subGain_ = 1.0;
    bool   enabled_ = false;

    // Default to Small everywhere except the sub: it is the safe assumption for PC speakers, and it
    // is what a receiver's setup routine picks for anything that isn't a full-range tower.
    bool small_[kChannels] = { true, true, true, false, true, true, true, true };

    Biquad hp1_[kChannels], hp2_[kChannels];
    Biquad lp1_[kChannels], lp2_[kChannels];
    Biquad subLp1_, subLp2_;
};

}  // namespace soundstage
