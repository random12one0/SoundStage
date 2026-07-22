// Soundstage DSP engine — a double-precision biquad (RBJ Audio EQ Cookbook).
//
// This is the first block of our own audio engine. It's portable, dependency-free C++17: it compiles
// and is unit-tested here on the Linux build host, and the exact same code runs inside the Windows
// engine that will host it. Double precision throughout — the fidelity you want when many EQ bands
// and effects are chained.
#pragma once

#include <cmath>
#include <complex>

namespace soundstage {

/// One second-order IIR section in Transposed Direct Form II (good numerical behaviour, a single
/// multiply-add chain per sample). Coefficients come from the RBJ cookbook; a0 is normalised out.
class Biquad {
public:
    void reset() noexcept { z1_ = 0.0; z2_ = 0.0; }

    /// Peaking (bell) EQ: `dbGain` at `f0`, unity far away. Centre-frequency magnitude is exactly
    /// 10^(dbGain/20) by construction.
    void setPeaking(double fs, double f0, double dbGain, double q) noexcept {
        const double A = std::pow(10.0, dbGain / 40.0);
        const double w0 = 2.0 * kPi * f0 / fs;
        const double cw = std::cos(w0);
        const double alpha = std::sin(w0) / (2.0 * q);
        normalise(1.0 + alpha * A, -2.0 * cw, 1.0 - alpha * A,
                  1.0 + alpha / A, -2.0 * cw, 1.0 - alpha / A);
    }

    void setLowShelf(double fs, double f0, double dbGain, double q) noexcept {
        const double A = std::pow(10.0, dbGain / 40.0);
        const double w0 = 2.0 * kPi * f0 / fs;
        const double cw = std::cos(w0);
        const double alpha = std::sin(w0) / (2.0 * q);
        const double tsa = 2.0 * std::sqrt(A) * alpha;
        normalise(A * ((A + 1.0) - (A - 1.0) * cw + tsa),
                  2.0 * A * ((A - 1.0) - (A + 1.0) * cw),
                  A * ((A + 1.0) - (A - 1.0) * cw - tsa),
                  (A + 1.0) + (A - 1.0) * cw + tsa,
                  -2.0 * ((A - 1.0) + (A + 1.0) * cw),
                  (A + 1.0) + (A - 1.0) * cw - tsa);
    }

    void setHighShelf(double fs, double f0, double dbGain, double q) noexcept {
        const double A = std::pow(10.0, dbGain / 40.0);
        const double w0 = 2.0 * kPi * f0 / fs;
        const double cw = std::cos(w0);
        const double alpha = std::sin(w0) / (2.0 * q);
        const double tsa = 2.0 * std::sqrt(A) * alpha;
        normalise(A * ((A + 1.0) + (A - 1.0) * cw + tsa),
                  -2.0 * A * ((A - 1.0) + (A + 1.0) * cw),
                  A * ((A + 1.0) + (A - 1.0) * cw - tsa),
                  (A + 1.0) - (A - 1.0) * cw + tsa,
                  2.0 * ((A - 1.0) - (A + 1.0) * cw),
                  (A + 1.0) - (A - 1.0) * cw - tsa);
    }

    void setLowpass(double fs, double f0, double q) noexcept {
        const double w0 = 2.0 * kPi * f0 / fs;
        const double cw = std::cos(w0);
        const double alpha = std::sin(w0) / (2.0 * q);
        normalise((1.0 - cw) / 2.0, 1.0 - cw, (1.0 - cw) / 2.0,
                  1.0 + alpha, -2.0 * cw, 1.0 - alpha);
    }

    void setHighpass(double fs, double f0, double q) noexcept {
        const double w0 = 2.0 * kPi * f0 / fs;
        const double cw = std::cos(w0);
        const double alpha = std::sin(w0) / (2.0 * q);
        normalise((1.0 + cw) / 2.0, -(1.0 + cw), (1.0 + cw) / 2.0,
                  1.0 + alpha, -2.0 * cw, 1.0 - alpha);
    }

    /// Process one sample (Transposed Direct Form II).
    inline double process(double x) noexcept {
        const double y = b0_ * x + z1_;
        z1_ = b1_ * x - a1_ * y + z2_;
        z2_ = b2_ * x - a2_ * y;
        return y;
    }

    /// Linear magnitude of the frequency response at `f` Hz — used by tests and (later) the UI curve.
    double magnitude(double fs, double f) const noexcept {
        const double w = 2.0 * kPi * f / fs;
        const std::complex<double> z1 = std::polar(1.0, -w);
        const std::complex<double> z2 = std::polar(1.0, -2.0 * w);
        const std::complex<double> num = b0_ + b1_ * z1 + b2_ * z2;
        const std::complex<double> den = 1.0 + a1_ * z1 + a2_ * z2;
        return std::abs(num / den);
    }

private:
    void normalise(double b0, double b1, double b2, double a0, double a1, double a2) noexcept {
        b0_ = b0 / a0;
        b1_ = b1 / a0;
        b2_ = b2 / a0;
        a1_ = a1 / a0;
        a2_ = a2 / a0;
    }

    static constexpr double kPi = 3.14159265358979323846;

    double b0_ = 1.0, b1_ = 0.0, b2_ = 0.0, a1_ = 0.0, a2_ = 0.0;
    double z1_ = 0.0, z2_ = 0.0;
};

}  // namespace soundstage
