# Soundstage architecture

## The three layers

```
Soundstage.Core     net8.0          ALL decision-making. OS-neutral, 165+ unit tests
                                    run on any platform (CI runs them on Linux + Windows).
Soundstage.Windows  net8.0-windows  Interop implementations of Core's interfaces:
                                    WASAPI (NAudio), ISpatialAudioClient COM, registry,
                                    Run-key autostart, peak metering.
Soundstage.App      net8.0-windows  WPF (WPF-UI Fluent, dark-first) — views, viewmodels,
                                    tray, hotkey, composition root. Kept thin.
```

Core defines every outward-facing dependency as an interface (`IFileSystem`, `IClock`,
`IDelayScheduler`, `IAudioEnvironmentSource`, `IApoEnvironment`, …); tests inject fakes and
simulate whole evenings of device switches and schedule boundaries deterministically.

## The config pipeline

```
SoundstageState ──ChainCompiler──► ApoDocument ──render──► chain.txt text
      │                 │
      │                 ├─ EffectCompilers (night/loudness/width/ambience → APO commands)
      │                 └─ HeadroomAnalyzer (biquad magnitude math → preamp auto-trim)
      │
      └─ ApplyOrchestrator: no-op detection → backup → atomic write → revert guard
```

- **`ApoCommand` AST** (`Core/Apo`): one record per config line, culture-invariant
  rendering (de-DE/tr-TR fuzz-tested — comma decimals are the classic EQ-tool bug), and a
  tolerant parser that preserves unknown lines verbatim.
- **`HeadroomAnalyzer`** (`Core/Dsp`): RBJ-cookbook biquads, worst-case chain gain over a
  512-point log grid, preamp clamped to keep peaks ≤ −0.5 dBFS. The UI's response curve
  uses the same math, so what you see is what safety sees.
- **`ChainCompiler`**: one `Device: {endpoint-guid}` section per enabled profile; the
  stereo-width matrix only compiles into 2-channel sections (hard guard).
- **`RevertGuard` policy**: manual applies of never-confirmed chains arm a countdown;
  confirmed chain hashes (persisted) skip the guard so preset switching stays instant;
  automation applies are never guarded (nobody is present to confirm — reverting would
  fight the rule engine; backups still capture every step).

## File ownership model

`config.txt` becomes a 3-line stub including `Soundstage\app.txt`; the switch file
includes `user.txt` (never regenerated — the hand-edit escape hatch) and `chain.txt`
(regenerated every apply). Bypass rewrites only the ~100-byte switch file. Ownership is
verified by content equality, so Peace or hand edits are detected as "control lost" and
re-taking control backs up the foreign content first. Disowning restores the newest
pre-takeover backup byte-for-byte.

## Automation engine

Pure function: `(rules, snapshot) → EvaluationResult`. Snapshot carries local time, device,
channel count, spatial state, and the set of processes with active audio sessions. Rules
evaluate top-to-bottom; each matching rule's actions land in a per-target map so the last
match wins per target; every effective action keeps a reference to the rule that
contributed it (per-action attribution — two rules matching at once must not credit the
wrong rule in the status readout). The coordinator debounces environment churn (2 s),
re-evaluates on a 30 s schedule tick, diffs desired vs current state, and only then calls
the controller.

## Bundled AutoEq database

`tools/AutoEqPacker` sparse-clones jaakkopasanen/AutoEq (blobless, ParametricEQ.txt files
only), parses each with the Core APO parser, and packs ~8,650 models into a 2.2 MB
minified JSON embedded into `Soundstage.Core`. Runtime search is token-AND with
exact/prefix/contains ranking. No network access at runtime, ever.

## CI

- **Linux job**: builds the *entire* solution (WPF cross-compiles via
  `EnableWindowsTargeting`) for fast markup/codegen signal, runs the Core suite.
- **Windows job**: full build, Core + App test suites (viewmodel smoke tests construct the
  real composition root on an audio-less, APO-less runner — guidance mode must not crash;
  XAML smoke tests parse every page on an STA thread), then publishes the single-file
  self-contained portable EXE as an artifact.
