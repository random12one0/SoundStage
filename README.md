<p align="center">
  <img src="docs/icon.png" width="96" alt="Soundstage icon" />
</p>

<h1 align="center">Soundstage</h1>

<p align="center">
  A modern front-end and automation layer for <a href="https://sourceforge.net/projects/equalizerapo/">Equalizer APO</a> on Windows.<br/>
  System-wide EQ, effects and safety — with the clean, calm UI this space never had.
</p>

---

Soundstage decides what goes into Equalizer APO's config file and when, and wraps that in
an interface a normal person can use. It replaces Peace GUI with something visually clean
and adds the automation and safety features Peace never had. It performs no DSP itself and
deliberately stays out of the way of Dolby Atmos for Home Theater — your spatial/upmixing
layer is untouched.

## Features

**Equalizer**
- Parametric EQ plus 10-band and 31-band graphic modes, with a live frequency-response plot
- Preset dropdown with instant apply — save, duplicate, rename, delete
- Five curated speaker presets out of the box (Flat Reference, Music, Film & Dialogue, Gaming, Spoken Word)
- **8,600+ headphone corrections bundled** from the [AutoEq](https://github.com/jaakkopasanen/AutoEq) project — search your model, click Add, done. No downloads.
- Imports Peace exports and AutoEq ParametricEQ files (including comma-decimal EU locale files)

**Effects** — each with one toggle and one intensity slider
- **Night mode** — a low-shelf bass cut (bass is what travels through walls) plus a gentle overall level reduction for late-night listening. 100% native — no plugins, so it can never interrupt your audio
- **Loudness compensation** — Equalizer APO's volume-tracking equal-loudness correction; restores bass and treble at low volume, runs happily alongside night mode
- **Stereo width** — mid/side widening applied to the front left/right pair only, so it works on any layout (2.0/5.1/7.1) yet never touches your centre, LFE or surround channels; the safe zone is made obvious

**Safety** — the reasons "APO made my audio crackle" won't happen here
- **Clipping protection**: the exact filter math is analyzed before every apply and the preamp is auto-trimmed so cumulative boosts can't clip; live clip indicator in the status bar
- **Confirm-or-revert**: a new, never-before-confirmed sound must be confirmed within 10 seconds or the previous known-good config comes back — a bad EQ can never leave you without audio
- **Backups of everything**: every replaced config is kept with one-click restore, including whatever existed before Soundstage took over

**Automation**
- Rules pair a trigger (time of day, app playing audio, channel-count change, device change) with actions (switch preset, toggle an effect, set intensity)
- Evaluated top to bottom, last match wins, with a live *"why is this active?"* explanation
- Five prebuilt rules ship disabled — night mode after 10pm, Music when Spotify plays, Film for browsers, Flat for multichannel, headphone profile on connect
- One master kill switch pauses all automation without deleting anything

**Per-device profiles** — speakers and headphones each keep their own presets, effects and rules; Soundstage follows the Windows default device automatically.

**Status readout** — always visible: active device, channel layout (2.0/5.1/7.1), sample rate/bit depth, spatial-audio (Atmos) state, active preset and which rule set it, headroom.

Plus: instant A/B bypass (button + global hotkey `Ctrl+Alt+B`), system tray with quick toggles, launch-on-boot, single portable EXE.

## Getting started

1. Install [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) on the output device(s) you want to control, and reboot.
2. Download `Soundstage.exe` from the latest CI build artifact (or build from source, below) and run it.
3. Click **Take control** — your existing config.txt is backed up first, always.
4. Pick a preset. Done.

> **Dolby Atmos users — do this once:** open **Diagnostics → Start 8-second test** while
> music plays. If the sound clearly muffles, APO is genuinely in your signal path with
> Atmos active. If not, the same page walks you through APO's SFX/EFX install-mode fix.
> Details in [docs/ATMOS-VALIDATION.md](docs/ATMOS-VALIDATION.md).

## Staying up to date

Soundstage checks GitHub for a newer release a few seconds after launch (toggle it off in
**Settings → Updates**) and shows a banner when one is available. **Settings → Updates**
also has a **Check for updates** button and a one-click **Download & install** that fetches
the new installer, verifies its SHA-256, runs it, and closes the app so it can update in
place — no manual GitHub visit needed.

> The in-app updater reads the repository's public Releases API. For it to work, this
> repository must be **public** (Settings → General → Change visibility on GitHub). If it's
> private, the updater will say so and you can still download releases manually.

## How it works

Soundstage owns `config.txt` with a three-line stub and keeps everything it generates in
its own subfolder:

```
config.txt                  ← stub: Include: Soundstage\app.txt   (original backed up)
config\Soundstage\app.txt   ← tiny switch file — bypass rewrites only this
config\Soundstage\user.txt  ← yours forever; hand-written lines that survive everything
config\Soundstage\chain.txt ← the generated processing chain (one section per device)
config\Soundstage\backups\  ← timestamped history
```

Equalizer APO watches included files, so every change applies live — bypass toggles by
rewriting ~100 bytes. Uninstalling Soundstage's control is one click (Diagnostics → Hand
back control) and restores your original config exactly.

## Building from source

```
dotnet build Soundstage.sln
dotnet test tests/Soundstage.Core.Tests        # runs anywhere, including Linux
dotnet test Soundstage.sln                     # full suite (Windows)
dotnet publish src/Soundstage.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

The core logic (config compilation, DSP analysis, automation engine) is an OS-neutral
library with 165+ unit tests; the WPF layer stays thin. See
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

To regenerate the bundled AutoEq database: `tools/AutoEqPacker` (sparse-clones the AutoEq
repo and packs ParametricEQ files into one JSON).

## Out of scope, on purpose

Upmixing/surround synthesis (Dolby Access does this), virtual 3D headphone surround, pitch
shifting, per-app volume, media playback, and anything involving bitstream passthrough.

## License

MIT. Bundled headphone correction data from [AutoEq](https://github.com/jaakkopasanen/AutoEq)
(MIT, © Jaakko Pasanen). Built with [WPF-UI](https://github.com/lepoco/wpfui),
[NAudio](https://github.com/naudio/NAudio), [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet),
and [Hardcodet NotifyIcon](https://github.com/hardcodet/wpf-notifyicon).
