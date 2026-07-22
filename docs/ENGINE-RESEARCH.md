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
