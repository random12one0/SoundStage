// Render tool: run a test signal through the Soundstage engine and write dry + wet WAVs, so the
// sound can be checked by ear long before the Windows app exists. Demo/preview only.
//
//   soundstage_render <out_dir>
//
// Writes <out_dir>/dry.wav and <out_dir>/reverb.wav.
#include "soundstage/Reverb.h"
#include "wav.h"

#include <algorithm>
#include <cmath>
#include <string>
#include <vector>

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr int kFs = 44100;

// A soft "pluck": a few harmonics with an exponential decay and a tiny noise transient on onset.
void addPluck(std::vector<double>& mono, double startSec, double freq, double amp, double decaySec) {
    const int start = static_cast<int>(startSec * kFs);
    const int len = static_cast<int>(decaySec * 3.0 * kFs);
    // deterministic tiny noise for the pluck attack (no rand — keeps the render reproducible)
    unsigned int seed = static_cast<unsigned int>(freq * 7.0 + 1.0);
    for (int n = 0; n < len; ++n) {
        const int idx = start + n;
        if (idx < 0 || idx >= static_cast<int>(mono.size())) continue;
        const double t = static_cast<double>(n) / kFs;
        const double env = std::exp(-t / decaySec);
        double s = std::sin(2.0 * kPi * freq * t)
                 + 0.5 * std::sin(2.0 * kPi * 2.0 * freq * t)
                 + 0.25 * std::sin(2.0 * kPi * 3.0 * freq * t);
        // short attack noise (~4 ms)
        if (n < kFs / 250) {
            seed = seed * 1664525u + 1013904223u;
            const double noise = (static_cast<double>(seed >> 9) / 8388608.0 - 1.0);
            s += noise * 0.6 * (1.0 - static_cast<double>(n) / (kFs / 250.0));
        }
        mono[idx] += amp * env * s * 0.5;
    }
}

}  // namespace

int main(int argc, char** argv) {
    const std::string dir = argc > 1 ? argv[1] : ".";
    const double totalSec = 4.2;
    const int frames = static_cast<int>(totalSec * kFs);
    std::vector<double> mono(frames, 0.0);

    // A little phrase: an A-major arpeggio, then the full chord left ringing so the tail blooms.
    const double A3 = 220.0, Cs4 = 277.18, E4 = 329.63, A4 = 440.0;
    addPluck(mono, 0.15, A3, 0.9, 0.45);
    addPluck(mono, 0.60, Cs4, 0.9, 0.45);
    addPluck(mono, 1.05, E4, 0.9, 0.45);
    addPluck(mono, 1.50, A4, 0.9, 0.45);
    // chord at ~2.1s, then silence to ~4.2s so the reverb tail is obvious against the quiet
    addPluck(mono, 2.10, A3, 0.7, 1.1);
    addPluck(mono, 2.10, Cs4, 0.7, 1.1);
    addPluck(mono, 2.10, E4, 0.7, 1.1);
    addPluck(mono, 2.10, A4, 0.7, 1.1);

    // normalise the source to a clean peak so nothing clips (the chord stacks four notes)
    double peak = 1e-9;
    for (double v : mono) peak = std::max(peak, std::fabs(v));
    const double norm = 0.72 / peak;
    for (double& v : mono) v *= norm;

    // dry: mono duplicated to stereo
    std::vector<double> dry(frames * 2, 0.0);
    for (int i = 0; i < frames; ++i) { dry[2*i] = mono[i]; dry[2*i+1] = mono[i]; }
    soundstage::writeWavStereo16((dir + "/dry.wav").c_str(), dry, kFs);

    // wet: our reverb, a warm hall
    soundstage::Reverb rev;
    rev.prepare(kFs);
    rev.setSize(0.72);
    rev.setDecaySeconds(2.4);
    rev.setDamping(0.45);
    rev.setPreDelayMs(22.0);
    rev.setWidth(0.85);
    rev.setMix(0.34);
    std::vector<double> wet(frames * 2, 0.0);
    for (int i = 0; i < frames; ++i) {
        double l = dry[2*i], r = dry[2*i+1];
        rev.process(l, r);
        wet[2*i] = l; wet[2*i+1] = r;
    }
    soundstage::writeWavStereo16((dir + "/reverb.wav").c_str(), wet, kFs);
    return 0;
}
