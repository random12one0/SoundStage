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

    // The tone shelves live in slots above the graphic bands (the app parks Warmth at 31 and Air at
    // 32). Those high slots must actually process, and must not disturb the graphic bands below them.
    {
        ssg_engine* t = ssg_create();
        ssg_prepare(t, 48000.0);
        ssg_enable_eq(t, 1);
        ssg_eq_set_num_bands(t, 33);
        for (int i = 0; i < 31; ++i) ssg_eq_set_band(t, i, SSG_BAND_PEAKING, 1000.0, 0.0, 1.0);

        // A 12 kHz tone, which a 10 kHz high shelf should lift.
        std::vector<float> hi(frames * 2), outFlat(frames * 2, 0.0f), outAir(frames * 2, 0.0f);
        for (int n = 0; n < frames; ++n) {
            const float s = 0.2f * std::sin(2.0f * 3.14159265f * 12000.0f * n / 48000.0f);
            hi[n * 2] = s; hi[n * 2 + 1] = s;
        }

        for (int b = 0; b < 8; ++b) ssg_process(t, hi.data(), 2, outFlat.data(), 2, frames);
        double flatPeak = 0.0;
        for (int i = 0; i < frames * 2; ++i) flatPeak = std::fmax(flatPeak, std::fabs((double)outFlat[i]));

        ssg_eq_set_band(t, 32, SSG_BAND_HIGH_SHELF, 10000.0, 8.0, 0.707);
        for (int b = 0; b < 8; ++b) ssg_process(t, hi.data(), 2, outAir.data(), 2, frames);
        double airPeak = 0.0;
        for (int i = 0; i < frames * 2; ++i) airPeak = std::fmax(airPeak, std::fabs((double)outAir[i]));

        check(airPeak > flatPeak * 1.5, "the Air slot (band 32) actually boosts a 12 kHz tone");

        ssg_destroy(t);
    }

    // Early reflections and modulation must each actually change the tail, and the reverb must stay
    // stable and finite with both pushed to their limits.
    {
        ssg_engine* t = ssg_create();
        ssg_prepare(t, 48000.0);
        ssg_enable_reverb(t, 1);
        ssg_reverb_set(t, 0.7, 2.5, 0.4, 20.0, 0.8, 1.0);   // fully wet, so we measure the tail alone

        std::vector<float> burst(frames * 2, 0.0f), tail(frames * 2, 0.0f);
        for (int n = 0; n < 64; ++n) {                       // a short click to excite the reverb
            const float s = 0.5f * std::sin(2.0f * 3.14159265f * 1000.0f * n / 48000.0f);
            burst[n * 2] = s; burst[n * 2 + 1] = s;
        }

        // The latest early tap is ~43 ms, so a single 512-sample block (10 ms) can't see them all.
        // Run several blocks and measure once the tail has actually developed.
        auto tailEnergy = [&](double early, double mod) {
            ssg_reset(t);
            ssg_reverb_set_character(t, early, mod);
            double sum = 0.0;
            for (int b = 0; b < 8; ++b) {
                ssg_process(t, burst.data(), 2, tail.data(), 2, frames);
                if (b >= 2) {
                    for (int i = 0; i < frames * 2; ++i) sum += (double)tail[i] * (double)tail[i];
                }
            }
            return sum;
        };

        const double noEarly = tailEnergy(0.0, 0.0);
        const double withEarly = tailEnergy(1.0, 0.0);
        check(withEarly > noEarly * 1.2, "early reflections add energy to the tail");

        // Modulation shouldn't change the level much, but it must change the waveform.
        ssg_reset(t);
        ssg_reverb_set_character(t, 0.0, 0.0);
        std::vector<float> flat(frames * 2, 0.0f);
        for (int b = 0; b < 6; ++b) ssg_process(t, burst.data(), 2, flat.data(), 2, frames);
        ssg_reset(t);
        ssg_reverb_set_character(t, 0.0, 1.0);
        std::vector<float> moved(frames * 2, 0.0f);
        for (int b = 0; b < 6; ++b) ssg_process(t, burst.data(), 2, moved.data(), 2, frames);
        double diff = 0.0;
        bool allFinite = true;
        for (int i = 0; i < frames * 2; ++i) {
            diff += std::fabs((double)flat[i] - (double)moved[i]);
            if (!std::isfinite(moved[i])) allFinite = false;
        }
        check(allFinite, "modulated reverb stays finite");
        check(diff > 1e-3, "modulation actually moves the delay lines");

        ssg_destroy(t);
    }

    // A real 5.1 source must survive as 5.1 — every channel distinct, nothing folded into the
    // fronts, and the centre/surrounds must not be silently dropped.
    {
        ssg_engine* t = ssg_create();
        ssg_prepare(t, 48000.0);

        // Six channels, each carrying a different level so we can tell them apart on the way out.
        const float level[6] = {0.50f, 0.45f, 0.40f, 0.35f, 0.30f, 0.25f};
        std::vector<float> in6(frames * 6), out6(frames * 6, 0.0f);
        for (int n = 0; n < frames; ++n) {
            const float s = std::sin(2.0f * 3.14159265f * 220.0f * n / 48000.0f);
            for (int c = 0; c < 6; ++c) in6[n * 6 + c] = s * level[c];
        }

        ssg_process_mc(t, in6.data(), 6, out6.data(), 6, frames);

        double peak[6] = {0.0};
        bool finite = true;
        for (int n = 0; n < frames; ++n) {
            for (int c = 0; c < 6; ++c) {
                const double v = out6[n * 6 + c];
                if (!std::isfinite(v)) finite = false;
                peak[c] = std::fmax(peak[c], std::fabs(v));
            }
        }

        check(finite, "multichannel output is finite");
        bool allPresent = true, ordered = true;
        for (int c = 0; c < 6; ++c) {
            if (peak[c] < 0.05) allPresent = false;
            if (c > 0 && peak[c] > peak[c - 1] + 0.02) ordered = false;
        }
        check(allPresent, "every channel of a 5.1 source reaches the output");
        check(ordered, "channels keep their relative levels (nothing is folded together)");

        // Default state is transparent, so a 5.1 source should come out essentially untouched.
        double worst = 0.0;
        for (int n = 0; n < frames; ++n) {
            for (int c = 0; c < 6; ++c) {
                worst = std::fmax(worst, std::fabs((double)out6[n * 6 + c] - (double)in6[n * 6 + c]));
            }
        }
        check(worst < 1e-4, "a 5.1 source passes through untouched with every effect off");

        // A speaker trim must move only its own channel.
        ssg_set_channel_trim_db(t, 2, -20.0);
        for (int b = 0; b < 8; ++b) ssg_process_mc(t, in6.data(), 6, out6.data(), 6, frames);
        double trimmedC = 0.0, untouchedBL = 0.0;
        for (int n = 0; n < frames; ++n) {
            trimmedC = std::fmax(trimmedC, std::fabs((double)out6[n * 6 + 2]));
            untouchedBL = std::fmax(untouchedBL, std::fabs((double)out6[n * 6 + 4]));
        }
        check(trimmedC < peak[2] * 0.25, "trimming the centre attenuates the centre");
        check(std::fabs(untouchedBL - peak[4]) < 1e-3, "trimming the centre leaves the surrounds alone");

        ssg_destroy(t);
    }

    // prepare() must not throw away settings. It runs when the audio path opens and on every device
    // or sample-rate change, so if it reset the enables it would silently switch off whatever the
    // user had turned on — the EQ section used to die the instant audio started.
    {
        ssg_engine* t = ssg_create();
        ssg_prepare(t, 48000.0);
        ssg_enable_eq(t, 1);
        ssg_eq_set_num_bands(t, 33);
        for (int i = 0; i < 31; ++i) ssg_eq_set_band(t, i, SSG_BAND_PEAKING, 1000.0, 0.0, 1.0);
        ssg_eq_set_band(t, 32, SSG_BAND_HIGH_SHELF, 10000.0, 8.0, 0.707);

        std::vector<float> hi(frames * 2), before(frames * 2, 0.0f), after(frames * 2, 0.0f);
        for (int n = 0; n < frames; ++n) {
            const float s = 0.2f * std::sin(2.0f * 3.14159265f * 12000.0f * n / 48000.0f);
            hi[n * 2] = s; hi[n * 2 + 1] = s;
        }

        for (int b = 0; b < 8; ++b) ssg_process(t, hi.data(), 2, before.data(), 2, frames);
        double peakBefore = 0.0;
        for (int i = 0; i < frames * 2; ++i) peakBefore = std::fmax(peakBefore, std::fabs((double)before[i]));

        ssg_prepare(t, 48000.0);   // e.g. the audio device opening
        for (int b = 0; b < 8; ++b) ssg_process(t, hi.data(), 2, after.data(), 2, frames);
        double peakAfter = 0.0;
        for (int i = 0; i < frames * 2; ++i) peakAfter = std::fmax(peakAfter, std::fabs((double)after[i]));

        check(std::fabs(peakAfter - peakBefore) < 0.02,
              "prepare() keeps the EQ enabled instead of silently bypassing it");

        // Same for the master gain, which prepare() also used to reset to unity.
        ssg_set_output_gain_db(t, -20.0);
        for (int b = 0; b < 8; ++b) ssg_process(t, hi.data(), 2, after.data(), 2, frames);
        ssg_prepare(t, 48000.0);
        for (int b = 0; b < 8; ++b) ssg_process(t, hi.data(), 2, after.data(), 2, frames);
        double quiet = 0.0;
        for (int i = 0; i < frames * 2; ++i) quiet = std::fmax(quiet, std::fabs((double)after[i]));
        check(quiet < peakBefore * 0.3, "prepare() keeps the output gain the host set");

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
