# Soundstage APO

Our own audio plugin, so Soundstage no longer needs VB-CABLE.

## What this replaces

Until now Soundstage worked by asking you to install a virtual audio cable, set it as your default
device, capture from it, process, and play back to the real speakers. That works, but it costs a
third-party install, a default-device dance every time you switch outputs, and a permanent extra
buffer of latency.

An **APO** (Audio Processing Object) is the slot Windows itself provides for code that sits inside the
audio engine for a playback device. Windows hands us the buffer on its way to the speakers, we
process it in place, it carries on. Nothing to route, nothing else to install.

Note this is *not* Equalizer APO. The old Soundstage wrote text into Equalizer APO's config file and
let that program do the work — a remote control for someone else's tool. Here the DSP is ours: the
same `EngineChain` the desktop app uses, compiled directly into the plugin.

## How the two halves talk

The app runs as you. The plugin runs inside `audiodg.exe`, a protected process in **session 0**. They
cannot call each other, so the app publishes its settings and the plugin reads them:

```
Soundstage.exe  ──writes──>  C:\ProgramData\Soundstage\engine-state.bin  ──reads──>  audiodg.exe
   ApoBridge.cs                    (fixed 8 KB block)                          SoundstageApo.cpp
```

A **file**, not a named shared-memory object. Named objects are session-scoped: a `Local\` name
created by the app is invisible from session 0, and `Global\` needs a privilege an ordinary account
does not have. A file has no session scope. After mapping it is plain memory — nothing touches the
disk on the audio path.

Coordination is a **seqlock**, because the reader is on a real-time thread and cannot take a lock.
The counter goes odd before a write, even after. A reader that sees odd — or sees the counter change
across its copy — skips that update. Settings landing one buffer late is inaudible; acting on half an
update would not be.

The two struct layouts must agree byte for byte. They are verified against each other rather than
assumed: see "Verifying the layout" below.

## Constraints this code is written around

- **Real-time thread, hard deadline.** `APOProcess` allocates nothing, takes no locks, and calls
  nothing that can block. Every buffer is reserved in `LockForProcess`. Notably `SyncSettings` does
  *not* retry opening the state file — that would put a blocking syscall on the audio thread every
  buffer just because the app isn't installed yet.
- **A crash here kills all system audio,** not just our app. Hence the checks on every buffer and the
  "if anything looks wrong, copy input to output" fallback. Degrade to untouched audio, never to a
  dead audio service.
- **Cost.** The full chain at 7.1 measures ~3% of one core, about 0.3 ms against a 10 ms budget.

## Building

```bash
powershell -ExecutionPolicy Bypass -File native/soundstage-apo/build.ps1
```

Produces `SoundstageApo.dll` (~60 KB) beside the script, exporting the four COM entry points.

## Installing

Installing is separate and **needs Administrator**, because attaching a plugin to a playback device
is a machine-wide change:

```bash
powershell -ExecutionPolicy Bypass -File native/soundstage-apo/install-apo.ps1
```

It lists your active playback devices, asks which one to process, copies the DLL to System32,
registers the COM class, and writes the effect properties for that endpoint.

**Every value it overwrites is backed up first** under a `Soundstage.Backup` key beside it. Those
properties are how the manufacturer's own audio software hooks in, and clobbering them with no way
back is how a device quietly loses its features. To undo everything:

```bash
powershell -ExecutionPolicy Bypass -File native/soundstage-apo/install-apo.ps1 -Uninstall
```

### If audio goes wrong

Run the uninstall above. Windows also disables an effect that misbehaves rather than breaking your
sound, so the usual failure is "no effects", not "no audio". Check Event Viewer under
**Windows Logs → System** for `audiodg` if the plugin stops loading.

## Verifying the layout

The C# writer and C++ reader agreeing on padding is exactly the kind of thing that fails silently, so
it is checked end to end rather than by eye: C# publishes a sentinel value into every field through
the real `ApoBridge`, and a C++ program reads the file back through the real `SoundstageApo.h`. All 33
sentinels — including `eqBands[35]`, the last element of the largest array — must round-trip.

Matching offsets alone would not be enough; a field with the wrong *type* has the right offset and the
wrong value. Comparing values catches both.

## Status

| | |
|---|---|
| Builds | yes — 60 KB, four COM exports verified present |
| Struct layout C# ↔ C++ | verified, 33/33 sentinels round-trip |
| App publishes live settings | verified — counter advances, stays even, values correct |
| Loaded by `audiodg.exe` | **not yet tested** — needs an elevated install |

The last row is the honest gap: attaching an APO to an endpoint requires Administrator, so the
install script has been written but not run. Everything up to that boundary is tested.
