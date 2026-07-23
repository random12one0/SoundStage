// Tests the flat C ABI the app links against — driving soundstage_engine exactly the way the C#
// host will: create, prepare, push buffers, turn knobs, read the meter, destroy. If this passes on
// the build host, the library the app P/Invokes is sound.
#include "soundstage/engine_c.h"

#include <cmath>
#include <cstdio>
#include <vector>

static int g_failures = 0;
static void check(bool cond, const char* what) {
    if (!cond) { std::printf("  FAIL: %s\n", what); ++g_failures; }
}

int main() {
    check(ssg_abi_version() == SSG_ABI_VERSION, "ABI version matches header");

    ssg_engine* e = ssg_create();
    check(e != nullptr, "engine created");
    ssg_prepare(e, 48000.0);

    const int frames = 512;
    std::vector<float> in(frames * 2), out(frames * 2, 0.0f);
    for (int n = 0; n < frames; ++n) {
        const float s = 0.25f * std::sin(2.0f * 3.14159265f * 440.0f * n / 48000.0f);
        in[n * 2] = s;
        in[n * 2 + 1] = s;
    }

    // Transparent by default: master on, every effect off -> stereo out == in.
    ssg_process(e, in.data(), 2, out.data(), 2, frames);
    bool passthrough = true, finite = true;
    for (int i = 0; i < frames * 2; ++i) {
        if (!std::isfinite(out[i])) finite = false;
        if (std::fabs(out[i] - in[i]) > 1e-6f) passthrough = false;
    }
    check(finite, "output is finite");
    check(passthrough, "default engine is a clean pass-through (stereo)");

    // Turn on a big EQ boost + drive the knobs; output must stay finite and clamped in [-1,1].
    ssg_eq_set_num_bands(e, 2);
    ssg_eq_set_band(e, 0, SSG_BAND_LOW_SHELF, 120.0, 8.0, 0.707);
    ssg_eq_set_band(e, 1, SSG_BAND_HIGH_SHELF, 9000.0, 5.0, 0.707);
    ssg_bass_set(e, 0.7, 90.0, 3.0);
    ssg_compressor_set(e, -22.0, 4.0, 6.0, 6.0, 12.0, 150.0);
    ssg_width_set(e, 1.6);
    ssg_reverb_set(e, 0.6, 1.8, 0.5, 18.0, 0.8, 0.3);
    ssg_set_output_gain_db(e, 3.0);
    ssg_enable_eq(e, 1);
    ssg_enable_bass(e, 1);
    ssg_enable_compressor(e, 1);
    ssg_enable_width(e, 1);
    ssg_enable_reverb(e, 1);

    bool inRange = true;
    finite = true;
    for (int block = 0; block < 200; ++block) {
        ssg_process(e, in.data(), 2, out.data(), 2, frames);
        for (int i = 0; i < frames * 2; ++i) {
            if (!std::isfinite(out[i])) finite = false;
            if (out[i] < -1.0001f || out[i] > 1.0001f) inRange = false;
        }
    }
    check(finite, "full-chain output stays finite through the ABI");
    check(inRange, "full-chain output stays within [-1, 1]");
    check(ssg_meter_reduction_db(e) >= 0.0, "gain-reduction meter is non-negative");

    // Surround expansion through the ABI: 8-channel out, fronts carry the signal.
    ssg_enable_upmix(e, 1);
    std::vector<float> out8(frames * 8, 0.0f);
    for (int block = 0; block < 20; ++block) ssg_process(e, in.data(), 2, out8.data(), 8, frames);
    bool frontsLive = false;
    for (int n = 0; n < frames; ++n) {
        if (std::fabs(out8[n * 8]) > 1e-4f || std::fabs(out8[n * 8 + 1]) > 1e-4f) frontsLive = true;
    }
    check(frontsLive, "7.1 output through the ABI drives the front channels");

    ssg_destroy(e);

    // Speaker trims: a channel pulled down must actually come out quieter, and the trims must be
    // independent (trimming the right speaker leaves the left alone).
    {
        ssg_engine* t = ssg_create();
        ssg_prepare(t, 48000.0);
        std::vector<float> flat(frames * 2, 0.0f), trimmed(frames * 2, 0.0f);
        ssg_process(t, in.data(), 2, flat.data(), 2, frames);

        ssg_set_channel_trim_db(t, 1, -20.0);      // right speaker only
        for (int block = 0; block < 8; ++block) {  // let the ramp settle
            ssg_process(t, in.data(), 2, trimmed.data(), 2, frames);
        }

        double peakL = 0.0, peakR = 0.0, refL = 0.0, refR = 0.0;
        for (int n = 0; n < frames; ++n) {
            peakL = std::fmax(peakL, std::fabs((double)trimmed[n * 2]));
            peakR = std::fmax(peakR, std::fabs((double)trimmed[n * 2 + 1]));
            refL  = std::fmax(refL,  std::fabs((double)flat[n * 2]));
            refR  = std::fmax(refR,  std::fabs((double)flat[n * 2 + 1]));
        }
        check(std::fabs(peakL - refL) < 1e-5, "trimming one speaker leaves the other untouched");
        check(peakR < refR * 0.2, "a -20 dB speaker trim actually attenuates that channel");

        ssg_set_channel_trim_db(t, 1, 0.0);
        for (int block = 0; block < 8; ++block) ssg_process(t, in.data(), 2, trimmed.data(), 2, frames);
        double backR = 0.0;
        for (int n = 0; n < frames; ++n) backR = std::fmax(backR, std::fabs((double)trimmed[n * 2 + 1]));
        check(std::fabs(backR - refR) < 1e-5, "returning a trim to 0 dB restores unity");

        ssg_set_channel_trim_db(t, 99, -6.0);  // out of range: must be ignored, not crash
        ssg_destroy(t);
    }

    // Null-safety: the ABI must tolerate a null handle without crashing (defensive host calls).
    ssg_prepare(nullptr, 48000.0);
    ssg_process(nullptr, in.data(), 2, out.data(), 2, frames);
    ssg_enable_eq(nullptr, 1);
    ssg_set_channel_trim_db(nullptr, 0, -3.0);
    ssg_destroy(nullptr);
    check(true, "null handle calls are no-ops (did not crash)");

    if (g_failures == 0) {
        std::printf("All engine C ABI tests passed.\n");
        return 0;
    }
    std::printf("%d C ABI assertion(s) failed.\n", g_failures);
    return 1;
}
