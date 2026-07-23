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

## Three things that must all be right, or nothing loads

Getting this running took finding three separate requirements, none of which produce a useful error
message when unmet. They are recorded here because each one costs an afternoon to rediscover.

**1. There are two registrations, not one.** Registering the COM class is necessary and not
sufficient. The audio engine looks plugins up in its own list at
`HKLM\SOFTWARE\Classes\AudioEngine\AudioProcessingObjects\{CLSID}`, holding a mirror of
`APO_REG_PROPERTIES` as registry values. A CLSID missing from that list is skipped in silence — no
event log entry, no failed stream, just an effect that never runs. `DllRegisterServer` writes both.

**2. The engine aggregates.** It creates the APO with a controlling `IUnknown`, so a class factory
that returns `CLASS_E_NOAGGREGATION` — which is what almost all COM boilerplate does — makes every
attempt to open a stream on that device fail outright.

**3. `IAudioSystemEffects3` is not optional.** A mode-effect APO is asked for it, and on Windows 11
refusing it does not degrade gracefully: `audiodg.exe` takes an access violation and all system audio
stops until the service restarts.

There is also a subtle fourth, in the aggregation itself. The non-delegating `QueryInterface` must
`AddRef` **through the pointer it returns**, not unconditionally on its own count. The interfaces
handed out have delegating `Release`, so counting acquisitions internally while releases go to the
outer object makes the two drift apart — the aggregate is freed while still in use, and audiodg dies
somewhere that looks nothing like the cause.

## Diagnosing it

`audiodg.exe` is a protected process: it cannot be debugged and its modules cannot be enumerated,
even from an elevated prompt. So the plugin narrates to `C:\ProgramData\Soundstage\apo.log` from its
setup and teardown paths — never from `APOProcess`, where opening a file would cause dropouts.

A healthy run looks like:

```
[load]   DLL mapped into pid=9696
[factory] instance constructed (aggregated=yes)
[init]   instance created, pid=9696, settings=connected
[lock]   RUNNING: 2 ch @ 48000 Hz, max 528 frames/buffer, settings=connected
[stats]  194400 frames, peak in -12.04 dBFS, peak out -32.04 dBFS, delta -20.00 dB
```

The `[stats]` line is the useful one: frames > 0 proves audio flowed through us, and the delta proves
we changed it. It measures only after the first half-second, because gain changes ramp and a peak
taken across the ramp-in would under-report the change no matter how large it was.

## Status

| | |
|---|---|
| Builds | yes — ~66 KB, four COM exports verified present |
| Struct layout C# ↔ C++ | verified, 33/33 sentinels round-trip |
| App publishes live settings | verified — counter advances, stays even, values correct |
| Loaded by `audiodg.exe` | **verified** — loads, aggregates, locks the stream |
| Reads settings from the app | **verified** — `settings=connected` |
| Actually processes audio | **verified** — asked for 0 / −20 / −6 dB, measured 0.00 / −20.00 / −6.00 |
| Bypass leaves audio untouched | **verified** — master off measures +0.00 dB |
| Analog endpoint (Realtek, 2 ch) | working |
| HDMI endpoint (NVIDIA → AV receiver, 8 ch) | **unconfirmed** — see below |

The HDMI row is honest rather than pessimistic. The plugin is attached to that endpoint and does get
constructed and initialised for it, but the receiver went into standby before a stream could be
locked, so the one thing left untested is `[lock]`/`[stats]` at 8 channels. Retest by playing
something with the receiver awake and reading `apo.log`.
