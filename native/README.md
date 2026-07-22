# native/ — Soundstage's own audio engine (C++)

Soundstage's audio DSP, built from scratch in portable C++17. This is the heart of the **v1.0
independent engine**: Soundstage runs its own **virtual audio device** so all system sound flows
through *this* DSP — no Equalizer APO, no third-party plug-in host, nothing flaky in the path. See
[`docs/ENGINE-RESEARCH.md`](../docs/ENGINE-RESEARCH.md) → *DECISION — v1.0 engine* for why.

## Why C++ and why separated

The real-time audio callback must never allocate or lock, which is why every engine of this class
(FxSound, Voicemeeter, Boom) is native C++. We mirror FxSound's clean split of **DSP core** from
**audio routing**:

- **`soundstage-dsp/`** — the portable, header-only DSP core. Pure math, no OS or driver
  dependencies, so it compiles and is **unit-tested on the Linux CI host** (`ctest`) exactly as it
  runs inside the Windows engine. Every effect lives here.
- *(next)* the **virtual-audio driver** (Windows/WDK, based on FxSound's open-source driver) that
  captures system audio and hands each buffer to this core.

## Building / testing locally

```sh
cmake -S native/soundstage-dsp -B native/soundstage-dsp/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/soundstage-dsp/build -j
ctest --test-dir native/soundstage-dsp/build --output-on-failure
```

CI runs exactly this on every push (Linux job).

## What's here today

- `Biquad` — double-precision RBJ-cookbook biquad (peaking, low/high shelf, low/high-pass) with a
  `magnitude()` helper for tests and the future UI curve. The building block of the EQ.
- `SmoothedValue` — per-sample linear parameter smoothing; the mechanism behind "can never pop".
- `Reverb` — our own reverb, an 8-line Feedback Delay Network (Hadamard feedback + per-line damping
  + input diffusion), with size / decay / damping / pre-delay / width / mix. This is the "Ambience".
- `Compressor` — feed-forward log-domain compressor/limiter (attack/release, soft knee, makeup); the
  "Leveler" and the guts of Night mode.
- `BassEnhancer` — psychoacoustic "virtual bass" via missing-fundamental harmonic synthesis.
- `StereoWidth` — mid/side width that leaves centred (mono) content untouched.
- `Upmix` — our own stereo → 5.1 / 7.1 (derived centre + LFE, filled surrounds/backs).
- Unit tests (10 groups): cookbook filter magnitudes; smoothing bounded/monotonic/exact; reverb
  decays to silence & bypasses clean; compressor pulls loud down / leaves quiet alone; width is
  mono-safe; upmix derives the right channels; bass adds harmonics above the fundamental.
- `tools/render.cpp` — runs a test signal through the engine and writes dry/wet WAVs, so the sound
  can be checked **by ear** before the Windows app exists.

## Coming next

The **virtual-audio driver** (Windows/WDK, based on FxSound's open-source one) that pipes all system
audio through this engine, then the **C# app + WebView2 UI** wired to it. That's the fresh v1.0 app —
a clean rebuild, nothing carried over from the old APO-based version.
