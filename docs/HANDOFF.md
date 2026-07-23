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

## Current state (v1.0.0-preview.3 shipped)
- First **working** build: power button starts/stops the audio path; effect dials are **on/off
  toggles** wired to the engine (Bass→bass, Ambience→reverb, Leveler→compressor, Air/Warmth→EQ
  shelves, Night→level trim). The whole DSP chain runs and is tested.
- UI feels native: custom title bar with working window buttons, fixed frame (only content scrolls),
  glow, old-app settings removed.

## What's next
1. **Drag-to-adjust** the dials + EQ bands + wire presets (currently toggle-only) — task #33.
2. Retune the effect→param curves by ear (user judges sound).
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
