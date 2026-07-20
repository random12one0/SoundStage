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
- [x] v0.5.0: surround-safe stereo width, effect tuning, dashboard status, global undo,
      better app/device detection, 13 genre presets, iOS-Shortcuts-style rule builder.

## ✅ v0.5.1 — critical hotfix (this release)

- [x] **Night mode can never silence audio again.** It's now 100% native (bass cut + gentle
      level reduction), with no VST plugin in the path. Root cause was a stale VST path in saved
      state pointing at a deleted DLL; Equalizer APO failed to load it and muted everything.
      Updating auto-heals the bad state.
- [x] **No more "Access to path is denied" crash.** Config writes retry briefly to ride out
      Equalizer APO's transient file lock, and any real permission failure shows a dismissible
      banner with a fix, instead of a scary error dialog.
- [x] Removed the confusing VST / LoudMax setup UI entirely.

---

## 🔜 v0.6.0 — priority: **effects that actually sound great** (+ status & UX)

Ordered by the priorities you picked. Items marked **Proposed** get a design walkthrough first.

### P2 — Effects rework (Boom-inspired, all native)   ← your #1 next priority
Boom's effects feel good because they commit to *confident, immediately-audible* settings. Ours
have been too timid. Plan:

- [ ] **Ambience — make it genuinely audible** (the "doesn't do much" fix). Boom's is a stereo
      reverb at a conservative wet level. We can beat it:
  - Real convolution reverb with a proper early-reflections + diffuse-tail impulse response.
  - Confident wet mix (~25–35%), not 5–10%.
  - **High-pass the reverb send (~200–300 Hz)** so bass stays tight and it never sounds muddy.
  - Pre-delay (~15–30 ms) so transients stay crisp and the space sounds larger.
  - **Per-channel decorrelation** on surround: different reflections per channel = true
    envelopment (something Boom's stereo model literally can't do). LFE stays dry.
  - Drop the "experimental" tag once it's solid.
- [ ] **Fidelity** (new, Boom-style one-knob "clarity"): a fixed presence + air lift
      (gentle boosts ~2–6 kHz and ~10–16 kHz) as a single macro. Easy, very audible.
- [ ] Re-tune night mode / loudness / width so 100% is unmistakable and 50% is the sweet spot.
- [ ] Consider a few more genre/scenario presets to match Boom's ~25 (Movie, Party, Deep,
      Dance/EDM, Loud, Treble Boost, …).

### P1 — "What's playing" status   ← the 2.0-vs-5.1 request
- [ ] **Show the incoming SOURCE format, not just the output.** Detect each app's stream channel
      count (`IAudioMeterInformation.GetMeteringChannelCount()`, best-effort) so the readout says
      e.g. **"Spotify · 2.0 source → upmixed to 7.1"** vs **"Netflix · 5.1 source · 7.1 output."**
- [ ] Move the important live status up to a compact top strip; slim the crowded bottom bar.

### P1 — UX cleanup
- [ ] **Automations "new rule" redesign** — cleaner card/step flow, less chrome. *(Proposed)*
- [ ] **12-hour time by default** in schedules, with a 12/24-hour toggle in Settings.
- [ ] **Start with ZERO automations.** The 5 prebuilt rules become **Quick-start cards** you add
      by tapping; nothing is active until you opt in. Lay the cards out as a **grid**.
- [ ] **Dashboard does more** — effect toggles right on the dashboard, so you don't have to
      switch tabs for the everyday stuff. *(Proposed)*

---

## 🌐 Public launch (P3)
- [ ] Rewrite the README/GitHub page for a public audience: what it is, screenshots, a clear
      setup guide (Equalizer APO → Soundstage → first-run → Atmos check), a "why does it pop?"
      note, troubleshooting, and download links.
- [ ] **Make the repo public** (also required for the in-app updater's GitHub Releases API).

---

## 💡 Feature ideas backlog (P4 — pick from later, don't build yet)
From the Boom 3D research + surround-specific synthesis. Difficulty is within native Equalizer
APO (no VST). Surround-first is where we can *out-feature* Boom.

**High-value, surround-exclusive (Boom can't do these):**
- [ ] Per-channel EQ (independent curves for Center / L-R / surrounds / LFE) — *medium*
- [ ] Dialog / center-clarity boost (presence EQ on Center only) — *easy-medium*, killer for movies
- [ ] Per-channel speaker trim + test tones (AV-receiver-style calibration) — *medium*
- [ ] Speaker distance / delay alignment (`Delay` per channel) — *medium*
- [ ] LFE / subwoofer gain + crossover — *medium*
- [ ] Per-channel room correction via imported measured IRs (REW/Dirac-style) — *hard*, an audiophile moat

**Boom parity, done surround-safely:**
- [ ] Equal-loudness low-volume enhancement (a static, channel-safe cousin of "night") — *easy*
- [ ] Master pre-amp / volume boost with headroom guard — *easy* (we have the guard)

**Explicitly NOT doing (conflicts with surround-first):**
- 3D headphone virtualization (HRTF) — it collapses multichannel into stereo; the opposite of our thesis.
- Forced upmix/downmix — channels stay put; Atmos owns upmixing.
- Pitch shift, per-app volume (Windows already does it), bundled radio/player — out of scope.

**A differentiator worth marketing:** reliability. Boom users report crackle, sleep-static, and
"support is nearly non-existent." *Rock-solid, no pops, no weird side-effects* is a real edge.

---

## Why does it pop when I change a setting?
Equalizer APO reloads its config the instant a file changes, and some audio hardware clicks on
reload. That pop is APO/driver behavior, not a Soundstage bug — we minimize it by never rewriting
an unchanged config. (Planned for v0.6.0: don't arm the confirm-or-revert guard on simple effect
toggles, so a first-time toggle is a single reload instead of apply-then-maybe-revert.)
