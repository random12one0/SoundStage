// Unit tests for the Soundstage DSP engine core. Dependency-free (no gtest) so it builds anywhere
// with a C++17 compiler — including this Linux build host, where we verify the math before it ships.
#include "soundstage/Biquad.h"
#include "soundstage/Reverb.h"
#include "soundstage/SmoothedValue.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <string>
#include <vector>

namespace {

int g_failures = 0;

void check(bool cond, const std::string& what) {
    if (!cond) {
        std::printf("  FAIL: %s\n", what.c_str());
        ++g_failures;
    }
}

void checkClose(double got, double want, double tol, const std::string& what) {
    if (std::fabs(got - want) > tol) {
        std::printf("  FAIL: %s (got %.9g, want %.9g, tol %.1g)\n", what.c_str(), got, want, tol);
        ++g_failures;
    }
}

constexpr double kFs = 48000.0;

double dbToLin(double db) { return std::pow(10.0, db / 20.0); }

// A peaking bell has its exact dbGain at the centre frequency and unity far away.
void testPeaking() {
    soundstage::Biquad b;
    b.setPeaking(kFs, 1000.0, 6.0, 1.0);
    checkClose(b.magnitude(kFs, 1000.0), dbToLin(6.0), 1e-6, "peaking magnitude at f0 == +6 dB");
    checkClose(b.magnitude(kFs, 20.0), 1.0, 1e-3, "peaking ~unity well below f0");
    checkClose(b.magnitude(kFs, 20000.0), 1.0, 1e-3, "peaking ~unity well above f0");

    soundstage::Biquad cut;
    cut.setPeaking(kFs, 1000.0, -6.0, 1.0);
    checkClose(cut.magnitude(kFs, 1000.0), dbToLin(-6.0), 1e-6, "peaking cut at f0 == -6 dB");
}

// Shelves reach their dbGain on their side of the corner and unity on the other.
void testShelves() {
    soundstage::Biquad ls;
    ls.setLowShelf(kFs, 200.0, 6.0, 0.707);
    checkClose(ls.magnitude(kFs, 10.0), dbToLin(6.0), 1e-3, "low shelf +6 dB at DC");
    checkClose(ls.magnitude(kFs, 20000.0), 1.0, 1e-3, "low shelf ~unity at top");

    soundstage::Biquad hs;
    hs.setHighShelf(kFs, 4000.0, 6.0, 0.707);
    checkClose(hs.magnitude(kFs, 23000.0), dbToLin(6.0), 1e-2, "high shelf +6 dB near Nyquist");
    checkClose(hs.magnitude(kFs, 50.0), 1.0, 1e-3, "high shelf ~unity at bottom");
}

// A Butterworth (Q=1/sqrt2) low-pass is -3 dB at cutoff and heavily attenuates an octave up.
void testLowpass() {
    soundstage::Biquad lp;
    lp.setLowpass(kFs, 1000.0, 0.70710678);
    checkClose(lp.magnitude(kFs, 20.0), 1.0, 1e-3, "lowpass passband ~unity");
    checkClose(lp.magnitude(kFs, 1000.0), dbToLin(-3.0103), 1e-3, "lowpass -3 dB at cutoff");
    check(lp.magnitude(kFs, 8000.0) < dbToLin(-24.0), "lowpass strongly attenuates 3 octaves up");
}

// Processing must stay finite and settle (no NaN/blow-up), and be flat when nothing is set.
void testProcessStable() {
    soundstage::Biquad b;
    b.setPeaking(kFs, 3000.0, 9.0, 2.0);
    double energy = 0.0;
    double x = 1.0;  // unit impulse, then silence
    for (int n = 0; n < 4096; ++n) {
        const double y = b.process(x);
        x = 0.0;
        check(std::isfinite(y), "impulse response sample is finite");
        energy += std::fabs(y);
    }
    check(energy > 0.0 && energy < 1e6, "impulse response has bounded energy");

    soundstage::Biquad flat;  // default = pass-through
    checkClose(flat.process(0.5), 0.5, 1e-12, "default biquad is unity pass-through");
}

// A smoothed value walks to its target in bounded, monotonic steps and lands exactly — the property
// that makes a pop impossible.
void testSmoothing() {
    soundstage::SmoothedValue s;
    s.reset(kFs, 0.01);  // 480-sample ramp
    s.setCurrentAndTarget(0.0);
    s.setTarget(1.0);
    check(s.isSmoothing(), "smoothing engaged after setTarget");

    double prev = s.current();
    double maxStep = 0.0;
    for (int n = 0; n < 480; ++n) {
        const double v = s.next();
        check(v >= prev - 1e-15, "smoothed value is monotonic non-decreasing");
        maxStep = std::max(maxStep, std::fabs(v - prev));
        prev = v;
    }
    checkClose(s.current(), 1.0, 1e-9, "smoothed value lands exactly on target");
    check(!s.isSmoothing(), "smoothing finished after ramp");
    checkClose(maxStep, 1.0 / 480.0, 1e-6, "per-sample step is bounded and constant");

    s.setCurrentAndTarget(0.25);
    check(!s.isSmoothing() && s.current() == 0.25, "setCurrentAndTarget jumps immediately");
}

// The reverb must stay finite, actually decay (a tail that dies away), and bypass cleanly at mix 0.
void testReverb() {
    soundstage::Reverb rev;
    rev.prepare(kFs);
    rev.setDecaySeconds(2.0);
    rev.setSize(0.7);
    rev.setDamping(0.5);
    rev.setPreDelayMs(20.0);
    rev.setWidth(0.8);
    rev.setMix(1.0);  // full wet, so we measure the tail

    // Impulse in, then silence.
    double eEarly = 0.0, eLate = 0.0, eVeryLate = 0.0;
    bool allFinite = true;
    double l = 1.0, r = 1.0;
    for (int n = 0; n < static_cast<int>(kFs * 5); ++n) {
        rev.process(l, r);
        if (!std::isfinite(l) || !std::isfinite(r)) allFinite = false;
        const double e = l * l + r * r;
        if (n < static_cast<int>(kFs * 0.3)) eEarly += e;
        else if (n < static_cast<int>(kFs * 1.5)) eLate += e;
        else if (n >= static_cast<int>(kFs * 4.0)) eVeryLate += e;
        l = 0.0; r = 0.0;  // no further input
    }
    check(allFinite, "reverb output stays finite (no blow-up)");
    check(eEarly > 0.0, "reverb produces a wet signal");
    check(eLate < eEarly, "reverb tail decays over time");
    check(eVeryLate < eLate * 1e-3, "reverb tail dies away to near silence");

    // Mix 0 must be an exact bypass (dry preserved).
    soundstage::Reverb dryRev;
    dryRev.prepare(kFs);
    dryRev.setMix(0.0);
    double a = 0.42, b = -0.17;
    dryRev.process(a, b);
    checkClose(a, 0.42, 1e-9, "reverb at mix 0 passes left through");
    checkClose(b, -0.17, 1e-9, "reverb at mix 0 passes right through");
}

struct Test { const char* name; void (*fn)(); };

}  // namespace

int main() {
    const std::vector<Test> tests = {
        {"peaking", testPeaking},
        {"shelves", testShelves},
        {"lowpass", testLowpass},
        {"process/stability", testProcessStable},
        {"smoothing", testSmoothing},
        {"reverb", testReverb},
    };

    for (const auto& t : tests) {
        std::printf("[ run ] %s\n", t.name);
        const int before = g_failures;
        t.fn();
        std::printf("[ %s ] %s\n", g_failures == before ? " ok " : "FAIL", t.name);
    }

    if (g_failures == 0) {
        std::printf("\nAll DSP engine core tests passed.\n");
        return 0;
    }
    std::printf("\n%d DSP core assertion(s) failed.\n", g_failures);
    return 1;
}
