# Soundstage — session handoff (current state)

Read this first when continuing the project (especially in a fresh/local Claude Code session).

## What Soundstage v1.0 is
A Windows desktop app that applies our **own** audio effects to everything playing on the PC — a
complete rebuild. **Nothing from the old Equalizer-APO version is used.** The old projects
(`src/Soundstage.App`, `src/Soundstage.Windows`, `src/Soundstage.Core`, `tests/*`) are the
pre-reset APO app; they still build as part of the solution but the v1.0 app does **not** depend on
them. Do not build new features on them.

## Architecture (v1.0)
```
your apps → [CABLE outlet] → EngineAudioHost (WASAPI capture) → native engine → your speakers
                                        UI (web) ⇄ C# controller ⇄ engine params
```
- **Native DSP engine** — `native/soundstage-dsp/` (header-only C++17: Biquad, Equalizer, BassEnhancer,
  Compressor, StereoWidth, Reverb (FDN), Upmix, SmoothedValue, EngineChain). Unit-tested (ctest).
- **C ABI shared library** — `native/soundstage-dsp/src/engine_c.cpp` + `include/soundstage/engine_c.h`
  → builds `soundstage_engine.dll` (the app P/Invokes it). Built via CMake, wired into the app build
  by `src/Soundstage.Shell/Soundstage.Shell.csproj` (BuildNativeEngine target).
- **App shell** — `src/Soundstage.Shell/` (WPF, net8.0-windows). Frameless window hosts the web UI in
  WebView2. `web/index.html` is the approved design (custom title bar, dials, EQ). `MainWindow.xaml.cs`
  bridges JS↔C#. `Engine/SoundstageEngine.cs` (P/Invoke wrapper), `Engine/EngineController.cs` (applies
  UI control messages, starts/stops audio), `Audio/EngineAudioHost.cs` (NAudio WASAPI capture→engine→render).

## Audio routing (v1 = CABLE; later = our own device)
v1 uses the free **VB-CABLE** virtual device as the "outlet": user sets *CABLE Input* as the Windows
default output; we WASAPI-loopback-capture it, process, and render to the real speakers.
Phase 3 replaces CABLE with our **own "Soundstage" virtual driver** (WDK; needs test-signing for dev,
EV cert for clean distribution) — task #29, deferred.

## Front end is wired (local Windows session, 22 Jul 2026)
Built and ran on the user's Windows machine for the first time — native engine + app both build,
`ctest` passes, and the app runs. Everything below was verified in the running app, not written blind.

**Working:** window drag (needed `IsNonClientRegionSupportEnabled`; the CSS alone did nothing) ·
dials turn by grabbing the handle and swinging it round the arc, hub click toggles, wheel nudges ·
EQ band handles drag, 10/31-band modes, preset curves (Flat/Cinema/Music/Podcast/Bass/Vocal) ·
user presets save/load/delete · volume dial · speaker calibration faders · speaker test bursts ·
Ambience page sliders + presets · upmix toggle · real device enumeration and switching ·
launch-at-login · level meter · state persisted to `%APPDATA%/Soundstage/state.json` and restored.

### Verified against real audio, not just the UI
A probe plays a known multi-tone signal into the CABLE outlet and loopback-captures the speakers,
so every claim below is a measurement (baseline = flat EQ, all effects off):

| control | measured | expected |
|---|---|---|
| Bass 96% | +4.0 dB @ 60 Hz, rest unmoved | low-band lift only |
| Warmth 96% | +7.6 dB @ 60 Hz, +2.3 @ 250 Hz | 200 Hz low shelf |
| Air 96% | +5.6 dB @ 12 kHz, +0.1 @ 4 kHz | 10 kHz high shelf |
| Night 95% | −11.4 dB flat | −12 × 0.95 |
| Leveler 50% | +1.6 dB | ~2 dB makeup |
| Volume 66 → 8 | −37.3 dB | −37.2 dB (square law) |
| EQ band 62.5 Hz +8.9 | +8.2 dB @ 60 Hz | bell at 62.5, Q 1.41 |
| FL trim −5.9 | −5.9 dB, left channel only | per-speaker trim |
| Music preset, 31-band | matches the designed curve | log-frequency sampling |

**This is how the `prepare()` bug was caught** — the UI looked right while Air did literally
nothing. Re-run the probe after touching the DSP; a control that looks wired is not evidence.

**Still a mockup:** the Automations page. The builder screens work as a designer, but nothing an
automation says actually fires — no trigger engine behind it. Now-playing text is honest
("Ready" / "Processing") rather than real track detection.

**Removed rather than faked:** the Ambience page's Early reflections, Modulation and Surround
spread sliders had no engine parameter behind them. Diffusion / low cut / high cut were *added* to
the reverb so those three could stay real.

## Earlier state (v1.0.0-preview.3 shipped)
- First **working** build: power button starts/stops the audio path; effect dials are **on/off
  toggles** wired to the engine (Bass→bass, Ambience→reverb, Leveler→compressor, Air/Warmth→EQ
  shelves, Night→level trim). The whole DSP chain runs and is tested.
- UI feels native: custom title bar with working window buttons, fixed frame (only content scrolls),
  glow, old-app settings removed.

## Just added in the cloud — VERIFY ON WINDOWS FIRST (written blind, not run)
The final cloud commits added these but could not be tested (no Windows to run on). **Run the app and
check each one first:**
- **Window dragging** via CSS `-webkit-app-region: drag` on `.tb` (buttons are `no-drag`). Replaces the
  old flaky DragMove-from-JS (which the user confirmed did NOT work). If the window still won't drag by
  the title bar in this WPF+WebView2 setup, fall back to a `WindowChrome` caption or host-side drag.
- **Drag-to-adjust dials**: `.radial` pointer events in `web/index.html` — drag up/down adjusts + emits
  to the engine, a plain click toggles on/off. Confirm dragging actually turns them and the value/arc
  updates.
- **Honest now-playing text** (was a hard-coded "Ivy — Frank Ocean"). Real now-playing detection
  (GlobalSystemMediaTransportControlsSessionManager) is still to do.

## What's next
1. Retune the effect→param curves by ear (user judges sound) — `EngineController.ApplyEffect`.
2. Make the Automations page real (trigger engine: time of day, app playing, device connected).
3. Our own **Soundstage virtual driver** to replace VB-CABLE — task #29.
4. Multichannel/surround render (v1 is stereo end-to-end).
5. Clean up / remove the old pre-reset projects.

## Build & run locally (Windows)
Prereqs: **.NET 8 SDK**, **CMake**, **Visual Studio Build Tools** (Desktop C++ / MSVC), **WebView2
runtime** (usually preinstalled). To test audio end-to-end: install **VB-CABLE** and set *CABLE Input*
as default output.
```
git checkout claude/windows-audio-control-app-vcgx9j
# build native tests:      cmake -S native/soundstage-dsp -B native/soundstage-dsp/build && cmake --build native/soundstage-dsp/build && ctest --test-dir native/soundstage-dsp/build
# run the app:             dotnet run --project src/Soundstage.Shell -c Release   (builds soundstage_engine.dll automatically)
# portable publish:        dotnet publish src/Soundstage.Shell -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
Working branch: **`claude/windows-audio-control-app-vcgx9j`**. Releases cut via the `release.yml`
workflow (workflow_dispatch with a version input) — but running locally you can just `dotnet run`.
