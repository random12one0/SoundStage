# Soundstage — Roadmap & To-Do

The shared, living to-do list. Checked items are shipped; unchecked are planned. Anything under
"Proposed" is a design we'll review together **before** building.

Guiding principle: **get what we have working the best before adding more.**

Positioning: Soundstage is *Boom 3D-style fun effects, but for surround-speaker setups* — it keeps
5.1/7.1 discrete and lets Dolby Atmos do the upmixing, instead of collapsing everything into a
headphone illusion the way Boom does.

---

## ✅ Shipped

- [x] Full app: EQ + presets, effects, automations, per-device profiles, diagnostics, tray,
      boot, backups/auto-revert, in-app updater, downloadable EXE via GitHub Releases.
- [x] **v0.5.x** — native (plugin-free) night mode that can never silence audio; no more
      "Access to path is denied" crash; surround-safe stereo width; dashboard status; global undo.
- [x] **v0.6.0** — incoming **source format** ("5.1 in" vs "2.0 in") on the dashboard + status bar;
      friendly "what's playing" incl. browser tab → **YouTube / Netflix / Twitch**; automation
      toast notifications.
- [x] **v0.7.0** — automations "new rule" rebuilt on tap-to-select **pills**; 12-hour time by
      default; **zero automations by default** (prebuilts are quick-start cards); dashboard drives
      the effects; Diagnostics "Copy my config".
- [x] **v0.8.0** — width mid/side scratch-channel matrix fix; bolder ambience IR; dashboard `%`
      readouts; softer selected pills.

## ✅ v0.10.0 — real fixes + surround calibration + a VST effect rack (this release)

- [x] **Stereo width — the actual root-cause fix.** Equalizer APO evaluates a single `Copy:` line
      in parallel, so the scratch/virtual channels we used were unnecessary and were themselves the
      likely cause of "everything collapses into one speaker" on 7.1. Replaced with a correct
      single-line L/R matrix that invents no channels and can't misroute surrounds.
- [x] **Ambience — the deeper fix.** The impulse response was a 2-channel *stereo* file, which APO
      can't map onto a 7.1 stream, so it silently dropped it. It's now a **mono** IR (APO applies it
      to every speaker) referenced by absolute path — so it finally loads on a surround receiver.
- [x] **Fidelity** — a one-knob clarity effect (native presence + air lift).
- [x] **Speaker calibration tab** — a per-channel level trim (turn the subwoofer down, balance the
      centre/surrounds) like an AV receiver. Attenuation-only, so it's always safe.
- [x] **Reset-to-stock** panic button in Settings (Flat preset, all effects off; undoable).
- [x] **VST effect rack (Enhancers)** — bundled Airwindows (MIT) plug-ins for the DSP native APO
      can't do: **Virtual Bass, Warmth, Air (a real exciter), Leveler (a real compressor), and a
      Loudness ceiling**. Parameters are driven via APO's `ChunkData` (verified byte-for-byte), each
      routed to the front L/R pair. One-click install downloads the pack and extracts just these
      five DLLs; missing plug-ins are skipped, never breaking audio.

## ✅ v0.9.0 — effects that sound right + the repeated asks

- [x] **Ambience actually works now.** The real bug: the impulse-response file was cached by name,
      so every rework since v0.6 was never written to disk — you kept hearing the old faint one.
      The IR file name now carries a **version token**, so a new algorithm always lands as a new
      file (and stale ones are cleaned up). The tail is now **long (~2.5 s)** and the wet level
      confident, so at high intensity it's an obvious hall that **keeps ringing out for a second or
      two after the music stops**, exactly as expected. No longer behind an "experimental" flag.
- [x] **Stereo width reworked to match what Boom's "Spatial" actually does.** It no longer remixes
      toward mono below 100% or over-widens into the "everything's in one speaker" zone above it.
      The slider is now **one-directional (0 → widest)** and **amplifies the left/right separation
      already in the recording**, capped at a musical, mono-safe maximum. Clearer wording in the UI
      so it's obvious what it does (and doesn't do — it never moves sounds between speakers).
- [x] **Smooth transitions (ramp) for every effect change.** Big night-mode / width / loudness
      changes now **ease in over a fraction of a second** instead of jumping, so they don't pop —
      **no matter the source** (you dragging a slider, a toggle, or an automation rule). On by
      default; a Settings switch turns it off for instant changes.
- [x] **Two completely separate preset dropdowns** — "Presets" (built-in) and "Your presets"
      (custom) — instead of one combined list. Plus **pinned quick-pick chips** for the common
      presets, so the everyday ones are one tap away without opening a dropdown.
- [x] **Clipping protection is now an on/off switch** in Settings. Off keeps the preamp at the
      preset's own level (for people who never push levels that hard). Night mode / ambience keep
      their own headroom either way.
- [x] **More app suggestions** in the automation app-trigger, plus an in-place explanation of how
      matching works (case-insensitive substring — "spot" matches Spotify; for browser streaming,
      match the browser).

### Still open / next
- [ ] **Night mode still drops audio on one Atmos rig** — root cause pending the user's copied
      config (the generated chain is provably just a bass cut + trim, so this is machine-specific).
- [ ] Move the full status to a top strip / further dashboard cockpit polish.
- [ ] By-ear confirmation of the new width + ambience + ramp on the user's 5.1/7.1 system.

---

## 🌐 Public launch (P3)
- [ ] Rewrite the README/GitHub page for a public audience: what it is, screenshots, a clear
      setup guide (Equalizer APO → Soundstage → first-run → Atmos check), a "why does it pop?"
      note, troubleshooting, and download links.
- [ ] **Make the repo public** (also required for the in-app updater's GitHub Releases API).

---

## 📋 Boom 3D feature inventory (research) — what to build, match, or skip

Full feature sweep of the current Windows Boom 3D, scored for native Equalizer APO feasibility.
Verdicts are surround-speaker-first. **We do not build these yet** — this is the menu.

| Boom feature | What it is | Native-APO? | Verdict for us |
|---|---|---|---|
| **31/10-band EQ + genre presets** | Graphic EQ + ~20 curated curves | Easy | **Have it.** (13 presets; could add more genres.) |
| **Spatial (Spatial Stereo)** | Widens by amplifying the L/R difference — no re-routing | Easy | **Have it** — this is our reworked Stereo width. |
| **Ambience** | Reverb/room via wet-mix | Medium (Convolution IR) | **Have it** — our convolution reverb. |
| **Volume Booster** | Gain past 100% with a limiter | Easy gain; no native limiter | **Maybe:** master boost with our headroom guard. |
| **Fidelity** | Fixed presence/air "clarity" contour | Easy–Medium | **Recommend next:** one-knob 2–6 kHz + 10–16 kHz lift. |
| **Stereo→5.1 upmix + per-channel/LFE level** | Play stereo out of all speakers; per-channel gain | Easy (Copy matrix + Preamp) | **Optional, off by default** — you've said Atmos owns upmix. |
| **Night Mode** | *Dynamic-range compression* | Not feasible (no native compressor) | **Diverge:** ours is honest bass-cut + level, not a compressor. |
| **3D Surround** | HRTF headphone virtualization | Medium–Hard | **Skip** — collapses multichannel to headphones; opposite of our thesis. |
| **Pitch shift** | Real-time ± semitones | Not feasible | **Skip.** |
| **Per-app volume** | WASAPI session control | N/A (not DSP) | **Skip** as an effect (Windows already does it). |
| **Player + internet radio** | Bundled media player | N/A | **Skip** — out of scope. |

**Net:** ~two-thirds of Boom's DSP maps cleanly onto native APO, and per-channel **surround
routing** is where we can beat Boom rather than match it. The only real gaps (Night compression,
Pitch, per-app volume) are non-linear or OS-session features outside the native-APO constraint.

---

## 💡 Feature ideas backlog (P4 — pick from later, don't build yet)
Surround-first is where we out-feature Boom. Difficulty is within native Equalizer APO (no VST).

**Recommended next adds (low-risk, audible):**
- [ ] **Fidelity** one-knob clarity (fixed presence + air lift) — *easy*.
- [ ] A few more genre/scenario presets to match Boom's ~25 (Party, Deep, Dance/EDM, Loud, …).
- [ ] Master pre-amp / volume boost with the existing headroom guard — *easy*.

**High-value, surround-exclusive (Boom can't do these):**
- [ ] Dialog / center-clarity boost (presence EQ on Center only) — *easy–medium*, killer for movies.
- [ ] Per-channel EQ (independent curves for Center / L-R / surrounds / LFE) — *medium*.
- [ ] Per-channel speaker trim + test tones, distance/delay alignment, LFE gain + crossover — *medium*.
- [ ] Per-channel room correction via imported measured IRs (REW/Dirac-style) — *hard*, an audiophile moat.
- [ ] **Optional native stereo→5.1/7.1 matrix** (no Atmos), off by default. NOT instrument separation.

**Explicitly NOT doing (conflicts with surround-first):**
- 3D headphone virtualization (HRTF); forced upmix/downmix; pitch shift; per-app volume; bundled radio.
- Real-time instrument/stem separation — not feasible system-wide, and not how Boom gets its sound.

**A differentiator worth marketing:** reliability — no pops, no sleep-static, no weird side effects.

---

## Why does it pop when I change a setting?
Equalizer APO reloads its config the instant a file changes, and a big jump in a filter or level
can click on reload. As of **v0.9.0**, Soundstage **ramps** large effect changes — it writes a
short series of small intermediate steps so the change eases in instead of jumping — for every
source (slider, toggle, automation). You can turn this off in Settings → *Smoothly ramp big effect
changes* if you'd rather have instant changes. We also never rewrite an unchanged config.
