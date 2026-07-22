# Audio-engine architecture — research findings

Deep, multi-source research pass on *how to build the system-wide audio engine* for Soundstage:
a surround-first (5.1/7.1 **speaker**) Windows effects engine — EQ + our own reverb, a real
compressor/limiter ("night mode"), bass enhancement, stereo width, and a stereo→5.1/7.1 upmix —
that coexists with Dolby Atmos, installs easily, and never pops.

Method: 27 sources fetched, 78 candidate claims, 25 adversarially verified (3-vote). Below are the
**9 confirmed** claims (primary sources) and what they mean for us. (The auto-synthesis step was cut
off by a session limit; this write-up is the synthesis.)

---

## Why our current Equalizer APO engine misbehaves on a 7.1 + Atmos rig — root causes, confirmed

1. **APO can only process uncompressed PCM — never a bitstream.** If Windows sends Dolby
   Digital/Atmos as an encoded *bitstream* to the receiver, Equalizer APO cannot touch it; the audio
   passes through untouched. This is the single biggest explanation for "Copy/convolution misbehave,
   reverb won't engage" on the Atmos setup — it isn't our bug, it's a hard Windows/APO limit.
   *(E-APO wiki, primary, 3-0)*
2. **APOs are COM, real-time, in-process objects** running inside Windows' `audiodg.exe`. Hosting a
   VST there means a plugin crash can take down the whole audio graph → matches "VST hosting is
   crash-prone." *(MS Learn, primary, 3-0)*
3. **APO is unsigned and needs the `DisableProtectedAudioDG` registry switch**, which also disables
   protected-content audio paths (DRM playback can go silent). *(E-APO wiki, primary, 3-0)*
4. **Channel-count changes (an upmix) are only legal in a Stream-effect (SFX) APO, pre-mix,
   per-stream** — never an endpoint effect. A stereo→7.1 upmix bolted onto the endpoint is
   architecturally wrong on Windows; it must sit *before* the mixer.
   *(MS Learn + dechamps/APO, primary, 3-0)*

## How the closest open-source analog (FxSound) actually works

5. **FxSound routes audio through a virtual audio driver** the user selects as default output — audio
   flows into their app, is processed, then rendered to the real device. **Not an APO, not loopback.**
   *(fxsound2/fxsound-app + fxsound-driver, primary, 3-0 / 2-0)*
6. **Its driver is derived from Microsoft's WDK "Virtual Audio Device Driver Sample"** — a concrete,
   copyable starting point if we go that route. *(fxsound-driver, primary, 2-0)*
7. **It cleanly separates routing (`audiopassthru`) from DSP (`DfxDsp`)** — the architecture to model:
   a device-I/O layer and a portable DSP core. *(fxsound-app, primary, 3-0)*
8. **FxSound is C++/C (~69% / ~30%), not .NET.** Real-time audio callbacks must never allocate or
   lock (Ross Bencina, "time waits for nothing"), which is why these engines are native C++.
   *(fxsound-app, primary, 3-0)*
9. Notably, FxSound's own "3D Surround" is a **psychoacoustic stereo widener, not a true multichannel
   speaker upmix** — i.e. even the reference app doesn't do surround-*speaker*-first. Our thesis
   (keep 5.1/7.1 discrete) is genuinely differentiated, but we own that DSP ourselves.

## DSP building blocks we can base our own effects on (well-sourced references)

- **EQ:** the Audio EQ Cookbook — closed-form biquad coefficients for all 9 filter types, one form.
- **Reverb (our own, from scratch):** an FDN (Signalsmith "Let's Write a Reverb") or a Dattorro
  plate. **RSAlgorithmicVerb** (open-source JUCE/C++) implements Dattorro plate/hall, Gardner rooms,
  and 4 FDNs in one codebase — a direct base for "make our own reverb."
- **Pop-free everything:** gain ramping / parameter smoothing / short crossfades on every parameter
  change — this is how you get a night mode that can never click, independent of engine choice.
- **Compressor/limiter, bass sub-harmonic synthesis, mid/side width, simple upmix** (Center = ½(L+R),
  surrounds = decorrelated Front L/R): all standard, all ours to write once we own the DSP core.

---

## The strategic fork (this is the decision to make before building)

The research's "ideal" answer is **a virtual audio driver + C++ DSP core, like FxSound** (option C).
But that is a *fundamentally larger* project than Soundstage is today. The three realistic paths:

### Option A — Evolve within Equalizer APO (C#, portable install)
Keep APO; drive only its reliable primitives (EQ, preamp, per-channel). Accept the bitstream limit
as a Windows truth and guide users to a PCM path. **Lowest effort, keeps the single-.exe install.**
Ceiling: can't make our own reverb, can't guarantee pop-free (APO reloads config → pop), bounded by
APO. Our own feature wishlist (own reverb, own upmix, never-pop) mostly *rules this out on its own*.

### Option B — Our own C++ DSP core, hosted as ONE native plugin inside APO ✅ recommended next step
We write a single native DSP plugin — our EQ + our reverb + compressor + bass + width + upmix, all
with parameter smoothing — and APO loads just that one, replacing the fragile chain of many Copy
lines + convolution + third-party VSTs. **Attacks the actual root causes** (fragile multi-primitive
configs; multiple crash-prone third-party VSTs; convolution reverb that won't engage) **without**
committing to a kernel driver, EV code-signing, C#-to-C# rewrite of the shell, or a heavy installer.
Compatible with "small verified steps" — build it one effect at a time and A/B against today's
version. Still inherits APO's unsigned + bitstream caveats. If those prove fatal, the debugged C++
core **ports straight into a driver** (FxSound's exact `DfxDsp` separation) — i.e. B is a stepping
stone to C, not a dead end.

### Option C — Full virtual audio driver + C++ engine (FxSound model)
Rock-solid, self-contained, independent of APO, full control. But: a kernel-mode WDM driver, an **EV
code-signing cert + Microsoft attestation/WHQL** signing process, a driver-installing (no longer
portable) installer, a C++ engine, and its own Atmos-coexistence design. A genuine "v1.0 from zero"
commitment — months, not weeks.

**Recommendation:** **B next, with C as the eventual destination if B's ceiling is hit.** It's the
smallest step that lets us actually own the DSP (our reverb, our upmix, never-pop) — which is what
your own feature list requires — while keeping the install light and the work incremental. We
validate the DSP first; we only take on the driver/signing world once the DSP is proven and the
APO caveats actually block *your* setup.

## Sources (primary/most-load-bearing)
- MS Learn — APO architecture: https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/audio-processing-object-architecture
- Equalizer APO documentation (PCM-only, DisableProtectedAudioDG): https://sourceforge.net/p/equalizerapo/wiki/Documentation/
- dechamps/APO — SFX/MFX/EFX, channel-count rules: https://github.com/dechamps/APO
- FxSound app + driver (virtual-driver model, module split, C++): https://github.com/fxsound2/fxsound-app · https://github.com/fxsound2/fxsound-driver
- Audio EQ Cookbook: https://webaudio.github.io/Audio-EQ-Cookbook/audio-eq-cookbook.html
- Signalsmith "Let's Write a Reverb": https://signalsmith-audio.co.uk/writing/2021/lets-write-a-reverb/
- RSAlgorithmicVerb (JUCE reverb families): https://github.com/reillypascal/RSAlgorithmicVerb
- Ross Bencina — real-time audio rules: http://www.rossbencina.com/code/real-time-audio-programming-101-time-waits-for-nothing

---

## DECISION — v1.0 engine (locked)

After a second research pass on the *implementation* stack, the earlier "Option B — our own C++
DSP as a plugin inside stock Equalizer APO" is **not buildable the clean way**, and we changed the
plan:

- **Our own VST2 is legally impossible.** Steinberg withdrew the VST2 SDK licence (Oct 2018) and its
  headers can't be redistributed. Stock APO only hosts VST2 — so we can't put *our* plugin in stock
  APO. (Bundling *pre-built* MIT VST2s like Airwindows is still fine; building a new one is not.)
- **VST3 works but needs a third-party APO fork** that is explicitly "for simple, lightweight
  plug-ins," adds latency, and crashes with some plugins — too fragile to be a foundation.
- The product owner's priority is unambiguous: **it must just work and not depend on flaky external
  pieces.** The APO route (which the prototype rode on) is exactly that flaky dependency, and it has
  been failing on the target 7.1 + Atmos rig.

**Chosen architecture (the FxSound / Boom / Voicemeeter model):**

1. **Our own virtual audio device (driver)** that the user selects as default output. It captures all
   system audio, hands it to our engine, and renders to the real device. No APO, no fork.
   Based on FxSound's **open-source** driver (derived from Microsoft's WDK virtual-audio sample).
2. **Our own DSP engine** (portable C++, this `native/soundstage-dsp` module) — EQ, our reverb,
   compressor/limiter, bass enhancement, stereo width, and the stereo→5.1/7.1 upmix. Double
   precision, per-sample parameter smoothing (can-never-pop).
3. **The app**: a C# shell hosting the approved UI in **WebView2** (reuses the exact HTML/CSS/JS
   design), bridged to the engine.

**The one real gate:** shipping the driver so it installs cleanly requires **attestation signing via
Microsoft Partner Center, which needs a paid EV code-signing certificate (~$250–400/yr)**. That is
the owner's to obtain, at ship time. For development the driver can run under Windows test-signing
mode. Everything else can be built and (for the DSP) unit-tested without it.

**Build order (small, verified steps):**
1. **DSP engine core** in portable C++, unit-tested on Linux CI. ← *in progress (this commit: biquad
   EQ + pop-free parameter smoothing).*
2. Grow the engine: our reverb (FDN), compressor/limiter, bass, width, surround upmix — each with
   tests.
3. The virtual-audio driver (Windows/WDK, based on the FxSound driver) routing audio through the
   engine.
4. C# app + WebView2 UI wired to the engine.

## Sources — implementation pass
- VST2 SDK withdrawn / non-redistributable: https://www.kvraudio.com/forum/viewtopic.php?t=508845
- Equalizer APO VST3 fork (fragile, "lightweight plug-ins only"): https://sourceforge.net/p/equalizerapo/discussion/general/thread/9526d91f79/
- CamillaDSP (open IIR/FIR engine, needs a virtual device on Windows): https://github.com/HEnquist/camilladsp
- Driver attestation signing needs an EV cert: https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/code-signing-attestation
- WebView2 in WPF (host our UI): https://learn.microsoft.com/en-us/microsoft-edge/webview2/get-started/wpf
