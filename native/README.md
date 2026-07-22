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

## What's here today (step 1 of the engine)

- `Biquad` — double-precision RBJ-cookbook biquad (peaking, low/high shelf, low/high-pass) with a
  `magnitude()` helper for tests and the future UI curve. The building block of the EQ.
- `SmoothedValue` — per-sample linear parameter smoothing; the mechanism behind "can never pop".
- Unit tests proving filter magnitudes match the cookbook and that smoothing is bounded, monotonic,
  and lands exactly on target.

## Coming next (each with tests)

Our reverb (FDN) · compressor/limiter · bass enhancement · stereo width · stereo→5.1/7.1 surround
upmix → then the virtual-audio driver that hosts it, then the C# app + WebView2 UI.
