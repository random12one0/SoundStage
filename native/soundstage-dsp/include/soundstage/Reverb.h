// Soundstage DSP engine — our own reverb, built from scratch.
//
// A Feedback Delay Network (Jot/FDN): eight delay lines mixed each pass by an energy-preserving
// Hadamard matrix, with a damping low-pass in every feedback path (so highs decay faster than lows,
// like a real room) and an input diffusion allpass chain (so it builds density instead of a fluttery
// echo). This is the standard architecture behind good algorithmic halls — references in
// docs/ENGINE-RESEARCH.md (Signalsmith "Let's Write a Reverb", RSAlgorithmicVerb).
//
// Everything is double precision. Parameters that a user can move (mix, size…) go through
// SmoothedValue elsewhere so they can never click.
#pragma once

#include <array>
#include <cmath>
#include <cstddef>
#include <vector>

namespace soundstage {

/// Simple power-of-two-free circular delay line (integer taps).
class DelayLine {
public:
    void prepare(std::size_t maxLen) {
        buf_.assign(maxLen + 1, 0.0);
        pos_ = 0;
    }
    void reset() { std::fill(buf_.begin(), buf_.end(), 0.0); pos_ = 0; }
    inline void push(double x) { buf_[pos_] = x; if (++pos_ >= buf_.size()) pos_ = 0; }
    inline double tap(std::size_t delay) const {
        std::size_t i = pos_ + buf_.size() - 1 - delay;
        while (i >= buf_.size()) i -= buf_.size();
        return buf_[i];
    }
    /// Fractional tap, linearly interpolated — what modulation needs: a delay length that slides
    /// smoothly instead of stepping between whole samples (which would click).
    inline double tapLerp(double delay) const {
        if (delay < 0.0) delay = 0.0;
        const std::size_t i0 = static_cast<std::size_t>(delay);
        const double frac = delay - static_cast<double>(i0);
        const double a = tap(i0);
        const double b = tap(i0 + 1);
        return a + (b - a) * frac;
    }
    std::size_t size() const { return buf_.size(); }
private:
    std::vector<double> buf_;
    std::size_t pos_ = 0;
};

/// One-pole low-pass, used to damp the reverb tail's high end.
class OnePoleLP {
public:
    void setCutoff(double fs, double hz) {
        const double x = std::exp(-2.0 * 3.14159265358979323846 * hz / fs);
        a_ = 1.0 - x; b_ = x;
    }
    inline double process(double in) { z_ = a_ * in + b_ * z_; return z_; }
    void reset() { z_ = 0.0; }
private:
    double a_ = 1.0, b_ = 0.0, z_ = 0.0;
};

/// Schroeder allpass, used for input diffusion (density without ringing).
class Allpass {
public:
    void prepare(std::size_t len) { d_.prepare(len); len_ = len; }
    void reset() { d_.reset(); }
    inline double process(double x) {
        const double delayed = d_.tap(len_ - 1);
        const double v = x + (-g_) * delayed;
        d_.push(v);
        return delayed + g_ * v;
    }
    void setG(double g) { g_ = g < 0.05 ? 0.05 : (g > 0.9 ? 0.9 : g); }
private:
    DelayLine d_; std::size_t len_ = 1; double g_ = 0.7;
};

/// One-pole high-pass — the complement of OnePoleLP. Used to keep the low end out of the reverb send
/// so the tail adds space without turning the bass to mud.
class OnePoleHP {
public:
    void setCutoff(double fs, double hz) {
        const double x = std::exp(-2.0 * 3.14159265358979323846 * hz / fs);
        a_ = 1.0 - x; b_ = x;
    }
    inline double process(double in) { z_ = a_ * in + b_ * z_; return in - z_; }
    void reset() { z_ = 0.0; }
private:
    double a_ = 1.0, b_ = 0.0, z_ = 0.0;
};

class Reverb {
public:
    static constexpr int N = 8;  // FDN size

    void prepare(double sampleRate) {
        fs_ = sampleRate;
        // Delay lengths in ms, mutually spread so echoes never line up (hall-ish, 19–68 ms).
        const double baseMs[N] = {19.1, 23.7, 29.3, 37.1, 43.9, 51.7, 59.3, 67.9};
        for (int i = 0; i < N; ++i) {
            baseLen_[i] = static_cast<std::size_t>(baseMs[i] * 0.001 * fs_);
            lines_[i].prepare(static_cast<std::size_t>(baseMs[i] * 0.001 * fs_ * 2.2) + 4);
            damp_[i].setCutoff(fs_, dampHz_);
        }
        // Input diffusion: four short allpasses (prime-ish lengths) build echo density.
        const double apMs[4] = {5.3, 7.1, 9.7, 12.9};
        for (int i = 0; i < 4; ++i) diff_[i].prepare(static_cast<std::size_t>(apMs[i] * 0.001 * fs_) + 1);
        // Pre-delay line, long enough to also host the early-reflection taps beyond it.
        pre_.prepare(static_cast<std::size_t>(0.32 * fs_) + 4);

        // Early reflection times (ms) and levels — an asymmetric, prime-ish scatter so they read as
        // a room rather than a rhythm, decaying as later bounces lose energy.
        const double erMs[kEarly] = {8.3, 13.7, 19.1, 26.3, 34.7, 43.1};
        for (int i = 0; i < kEarly; ++i) {
            earlyLen_[i] = static_cast<std::size_t>(erMs[i] * 0.001 * fs_);
            earlyGain_[i] = 0.62 * std::pow(0.72, static_cast<double>(i));
        }

        // Modulation LFOs: slow, and mutually irrational so the lines never move in lockstep.
        const double lfoHz[N] = {0.11, 0.17, 0.23, 0.29, 0.37, 0.41, 0.47, 0.53};
        for (int i = 0; i < N; ++i) {
            lfoInc_[i] = 2.0 * 3.14159265358979323846 * lfoHz[i] / fs_;
            lfoPhase_[i] = static_cast<double>(i) * 0.7853981633974483;   // spread the start phases
        }

        lowCut_.setCutoff(fs_, lowCutHz_);
        highCutL_.setCutoff(fs_, highCutHz_);
        highCutR_.setCutoff(fs_, highCutHz_);
        updateDecay();
        reset();
    }

    void reset() {
        for (auto& l : lines_) l.reset();
        for (auto& d : damp_) d.reset();
        for (auto& a : diff_) a.reset();
        pre_.reset();
        lowCut_.reset();
        highCutL_.reset();
        highCutR_.reset();
    }

    void setSize(double s01)      { size_ = clamp(s01, 0.05, 1.0); updateDecay(); }   // delay scale
    void setDecaySeconds(double s){ rt60_ = std::max(0.1, s); updateDecay(); }         // tail length
    void setDamping(double d01)   { dampHz_ = 1500.0 + (1.0 - clamp(d01, 0.0, 1.0)) * 16000.0;
                                    for (auto& d : damp_) d.setCutoff(fs_, dampHz_); }  // 0=dark..1=bright? see note
    void setPreDelayMs(double ms) { preLen_ = static_cast<std::size_t>(clamp(ms, 0.0, 200.0) * 0.001 * fs_); }
    void setMix(double wet01)     { mix_ = clamp(wet01, 0.0, 1.0); }
    void setWidth(double w01)     { width_ = clamp(w01, 0.0, 1.0); }

    /// Input diffusion, 0..1: how hard the allpass chain smears the input. Low = you can hear the
    /// individual early echoes; high = a smooth wash.
    void setDiffusion(double d01) {
        const double g = 0.35 + clamp(d01, 0.0, 1.0) * 0.43;   // 0.35..0.78
        for (auto& a : diff_) a.setG(g);
    }

    /// Low cut on the reverb send (Hz): keeps bass out of the tail so the low end stays tight.
    void setLowCutHz(double hz) { lowCutHz_ = clamp(hz, 20.0, 1000.0); lowCut_.setCutoff(fs_, lowCutHz_); }

    /// High cut on the wet output (Hz): rolls the top off the tail so it sits behind the dry signal.
    void setHighCutHz(double hz) {
        highCutHz_ = clamp(hz, 1000.0, 20000.0);
        highCutL_.setCutoff(fs_, highCutHz_);
        highCutR_.setCutoff(fs_, highCutHz_);
    }

    /// Early reflections, 0..1: how much of the discrete first bounces off the walls you hear before
    /// the diffuse tail arrives. This is most of what tells you how big a room is.
    void setEarlyLevel(double e01) { early_ = clamp(e01, 0.0, 1.0); }

    /// Modulation, 0..1: slowly detunes the delay lines. A static FDN rings on sustained notes; a
    /// little movement breaks up that metallic character. Too much and it sounds seasick.
    void setModulation(double m01) { mod_ = clamp(m01, 0.0, 1.0); }

    /// Process one stereo sample in place. Dry signal is preserved and blended per `mix`.
    inline void process(double& left, double& right) {
        const double dryL = left, dryR = right;

        // Mono send into the reverb, through low cut, pre-delay and input diffusion.
        double in = 0.5 * (dryL + dryR);
        in = lowCut_.process(in);
        pre_.push(in);
        in = preLen_ > 0 ? pre_.tap(preLen_) : in;
        for (auto& a : diff_) in = a.process(in);

        // Read the eight delay lines. With modulation on, each line's length breathes around its
        // nominal value on its own slow LFO, so no two lines drift together.
        double s[N];
        if (mod_ > 0.0) {
            const double depth = mod_ * 3.0;   // samples of swing — subtle by design
            for (int i = 0; i < N; ++i) {
                lfoPhase_[i] += lfoInc_[i];
                if (lfoPhase_[i] > 6.283185307179586) lfoPhase_[i] -= 6.283185307179586;
                const double d = static_cast<double>(len_[i] - 1) + std::sin(lfoPhase_[i]) * depth;
                s[i] = lines_[i].tapLerp(d < 1.0 ? 1.0 : d);
            }
        } else {
            for (int i = 0; i < N; ++i) s[i] = lines_[i].tap(len_[i] - 1);
        }

        // Two decorrelated output taps (alternating signs) → stereo.
        double wetL = 0.0, wetR = 0.0;
        for (int i = 0; i < N; ++i) {
            wetL += (i & 1 ? -s[i] : s[i]);
            wetR += ((i >> 1) & 1 ? -s[i] : s[i]);
        }
        wetL *= 0.35; wetR *= 0.35;

        // Early reflections: a handful of discrete taps off the pre-delay line, panned apart. They
        // arrive before the tail and carry most of the sense of room size.
        if (early_ > 0.0) {
            double eL = 0.0, eR = 0.0;
            for (int i = 0; i < kEarly; ++i) {
                const double t = pre_.tap(earlyLen_[i]);
                if (i & 1) { eR += t * earlyGain_[i]; } else { eL += t * earlyGain_[i]; }
            }
            wetL += eL * early_;
            wetR += eR * early_;
        }
        // High cut on the tail only — the dry signal keeps all of its top end.
        wetL = highCutL_.process(wetL);
        wetR = highCutR_.process(wetR);
        // Width: blend toward mono as width→0.
        const double mid = 0.5 * (wetL + wetR), side = 0.5 * (wetL - wetR) * width_;
        wetL = mid + side; wetR = mid - side;

        // Feedback: Hadamard-mix the lines, damp, scale by decay gain, add the input, write back.
        hadamard8(s);
        for (int i = 0; i < N; ++i) {
            double v = s[i] * fbGain_ + in;   // in is fed to every line
            v = damp_[i].process(v);
            lines_[i].push(v);
        }

        left  = dryL * (1.0 - mix_) + wetL * mix_;
        right = dryR * (1.0 - mix_) + wetR * mix_;
    }

private:
    static double clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }

    void updateDecay() {
        for (int i = 0; i < N; ++i) {
            len_[i] = std::max<std::size_t>(2, static_cast<std::size_t>(baseLen_[i] * size_));
        }
        // Feedback gain for the target RT60 (using an average delay length): g = 10^(-3*L/(RT60*fs)).
        double avg = 0.0; for (int i = 0; i < N; ++i) avg += static_cast<double>(len_[i]);
        avg /= N;
        fbGain_ = std::pow(10.0, -3.0 * avg / (rt60_ * fs_));
        if (fbGain_ > 0.999) fbGain_ = 0.999;  // stay stable
    }

    // In-place 8-point Hadamard (Walsh) transform, normalised by 1/sqrt(8): orthogonal, energy-preserving.
    static inline void hadamard8(double* v) {
        for (int step = 1; step < N; step <<= 1) {
            for (int i = 0; i < N; i += step << 1) {
                for (int j = i; j < i + step; ++j) {
                    const double a = v[j], b = v[j + step];
                    v[j] = a + b; v[j + step] = a - b;
                }
            }
        }
        const double norm = 0.35355339059327373;  // 1/sqrt(8)
        for (int i = 0; i < N; ++i) v[i] *= norm;
    }

    double fs_ = 48000.0;
    std::array<DelayLine, N> lines_;
    std::array<OnePoleLP, N> damp_;
    std::array<Allpass, 4> diff_;
    DelayLine pre_;
    OnePoleHP lowCut_;
    OnePoleLP highCutL_, highCutR_;
    std::size_t baseLen_[N] = {0}, len_[N] = {0}, preLen_ = 0;
    double size_ = 0.7, rt60_ = 2.0, dampHz_ = 9000.0, mix_ = 0.3, width_ = 0.8, fbGain_ = 0.8;
    double lowCutHz_ = 120.0, highCutHz_ = 8000.0;

    static constexpr int kEarly = 6;
    std::size_t earlyLen_[kEarly] = {0};
    double earlyGain_[kEarly] = {0.0};
    double early_ = 0.5, mod_ = 0.2;
    double lfoPhase_[N] = {0.0}, lfoInc_[N] = {0.0};
};

}  // namespace soundstage
