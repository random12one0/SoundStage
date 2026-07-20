# Validating Equalizer APO with Dolby Atmos for Home Theater

## The problem

Equalizer APO and Windows spatial audio (Dolby Atmos for Home Theater, DTS:X, Windows
Sonic) both insert into the same audio endpoint's processing chain. On some driver
configurations, enabling a spatial format makes Windows route audio through a path that
**bypasses custom APOs entirely** — Equalizer APO installs cleanly, reports no errors, and
silently does nothing.

If you run Dolby Atmos via the Dolby Access app (as this project's reference setup does —
a 5.1 system on an Onkyo TX-NR676 over HDMI), you must verify APO is actually in the
signal path **once, by ear**. Software cannot reliably detect this for you.

## The 60-second check

1. Make sure Dolby Atmos for Home Theater is **enabled** (Windows Settings → Sound →
   your output → Spatial audio), exactly as you normally use it.
2. Play any music or video with obvious treble content.
3. In Soundstage, open **Diagnostics** and press **Start 8-second test**.
   Soundstage temporarily applies an extreme EQ (−18 dB above 1 kHz — everything sounds
   like it's underwater) and automatically reverts after 8 seconds.
4. Judge by ear:
   - **Clearly muffled?** APO works with Atmos on. You're done — everything Soundstage
     does will function normally.
   - **No change at all?** APO is being bypassed. Continue below.

## If APO is being bypassed

1. Open **Equalizer APO's Configurator** (Start menu → "Configurator"), select your
   output device, and click **Troubleshooting options**.
2. Change the installation mode: check **"Install as SFX/EFX (experimental)"** instead of
   the default LFX/GFX. Spatial formats bypass the legacy stage on many drivers, while the
   SFX/EFX stage remains in the path.
3. Reboot, then run the 8-second test again.
4. Still nothing? Try the Configurator's other troubleshooting toggles ("Use original
   APO", pre-mix vs post-mix) one at a time, rebooting between changes.
5. **Last resort:** disable Dolby Atmos for Home Theater (set Spatial audio to Off).
   Equalizer APO then always works, but you lose Dolby's adaptive upmixing — a real
   trade-off that is yours to make. Most systems never reach this step.

## Why Soundstage can't just detect this

The Windows spatial pipeline reports whether a spatial format is *engaged* (Soundstage
shows this in the status bar), but not whether third-party APOs are inside the active
processing graph. The audibility test is the ground truth, which is why it ships as a
first-class feature on the Diagnostics page rather than a footnote.
