// Soundstage DSP engine — a per-sample linearly-smoothed parameter.
//
// This is how we guarantee "can never pop": no gain, mix, or coefficient ever jumps. A parameter
// change sets a target, and the value walks there one bounded step per sample over a ramp whose
// length we choose (a few ms for a knob; up to several seconds for the night-mode descent). Nothing
// downstream ever sees a discontinuity.
#pragma once

namespace soundstage {

class SmoothedValue {
public:
    /// Configure the sample rate and ramp time. Keeps the current value; call setCurrentAndTarget to
    /// also seed it.
    void reset(double sampleRate, double rampSeconds) noexcept {
        rampLen_ = rampSeconds > 0.0 ? static_cast<int>(sampleRate * rampSeconds + 0.5) : 0;
        if (rampLen_ < 1) {
            rampLen_ = 1;
        }
        countdown_ = 0;
        step_ = 0.0;
    }

    /// Jump immediately (no ramp) — for initial state or a hard reset.
    void setCurrentAndTarget(double v) noexcept {
        current_ = target_ = v;
        countdown_ = 0;
        step_ = 0.0;
    }

    /// Aim at a new value; the walk happens over the configured ramp.
    void setTarget(double v) noexcept {
        if (v == target_) {
            return;
        }
        target_ = v;
        step_ = (target_ - current_) / rampLen_;
        countdown_ = rampLen_;
    }

    /// Advance one sample toward the target and return the new current value.
    inline double next() noexcept {
        if (countdown_ > 0) {
            current_ += step_;
            if (--countdown_ == 0) {
                current_ = target_;  // land exactly, no drift
            }
        }
        return current_;
    }

    double current() const noexcept { return current_; }
    double target() const noexcept { return target_; }
    bool isSmoothing() const noexcept { return countdown_ > 0; }

private:
    double current_ = 0.0;
    double target_ = 0.0;
    double step_ = 0.0;
    int countdown_ = 0;
    int rampLen_ = 1;
};

}  // namespace soundstage
