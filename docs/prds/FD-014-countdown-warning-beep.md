# FD-014 — Countdown warning beep

Status: ready-for-agent

**Story:** _As an artist, I hear a short beep when ten seconds remain on a pose, so I can wrap up my marks without watching the clock._
**Depends on:** none

## Summary

A short audio cue plays once when the pose countdown crosses a warning threshold (default ten
seconds). The artist can enable or disable it independently of the existing transition chime
(`ChimeOnChange`). The warning fires only during a pose phase — never during a break.

## Model placement

| | |
|---|---|
| Context | Session Execution / Preferences |
| Owning object | `DrawingSession<TImage>` (threshold rule), `Settings` (preference) |
| New Core type | no |
| New invariants | `INV-CD-9` — a warning fires exactly once per pose, at the configured threshold |
| Invariants changed | `INV-SES-13` — `SessionTick` gains a `WarningReached` value (additive, no existing value changes meaning) |
| Crosses a boundary | `Settings.WarnSeconds` property; `SessionConfig` gains `WarnSeconds` field (intent extra) |

## Approach

- **Core:** `DrawingSession.Tick()` — when the phase is `Pose`, remaining time crosses from above
  `WarnSeconds` to at-or-below it, and the warning has not already fired for this pose, `Tick()`
  returns `SessionTick.WarningReached`. The flag resets on every pose advance (`Next`, `Skip`,
  break-to-pose transition). A `WarnSeconds` of zero disables the feature — `Tick` never returns
  the warning value.
- **Android:** `SessionActivity.Tick()` — on `WarningReached`, play a distinct short tone (different
  pitch or pattern from the transition chime so the artist can tell them apart by ear). The tone
  plays regardless of `ChimeOnChange` — the two settings are independent.
- **Resources:** new string `warn_seconds_label` for the setup screen toggle/field. Reuse existing
  `ToneGenerator` infrastructure from the transition chime.
- **Persistence:** `Settings.WarnSeconds` — `int`, default `0` (off). A positive value is the
  threshold in seconds; `0` means disabled.
- **Injected dependency:** none new — uses the existing injected clock via `Tick()`.

## Acceptance criteria

- [ ] With `WarnSeconds = 10` and a 30 s pose, a beep plays exactly once when the countdown
      display first shows ≤ 10 s — not on every subsequent tick
- [ ] The beep does not fire during a break phase
- [ ] The beep does not fire when the pose is paused at ≤ 10 s and then resumed (the crossing
      already happened)
- [ ] The beep fires again on the next pose (flag resets on advance)
- [ ] `WarnSeconds = 0` produces no beep, ever
- [ ] A pose shorter than the threshold (e.g. 5 s pose, 10 s warn) never fires the warning
      (`INV-CD-9`: remaining must cross *from above* the threshold)
- [ ] Skipping a pose before the threshold resets the flag — the next pose can still warn
- [ ] The warning is independent of `ChimeOnChange` — either, both, or neither can be on
- [ ] `CompletedCount`, `SkippedCount`, `TotalDrawingTime` — unchanged (this adds no counting
      effect)

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `DrawingSessionCountdownTests` — `INV-CD-9`: fires once at threshold, does not fire when pose < threshold, does not fire during break, resets on advance, disabled when zero. `SettingsTests` — `WarnSeconds` default and round-trip |
| Contract | `SessionScreenContractTests` — `SessionConfig` carries `WarnSeconds`; `UiResourceContractTests` — new string exists |
| E2E-model | `SessionE2ETests` — a full session with warnings enabled completes normally |
| UI (Appium) | `n/a` — the beep is audible-only; Core coverage is sufficient |

## Out of scope

- Configurable threshold beyond on/off (artist picks how many seconds) — could follow as an
  enhancement; for now fixed at 10 s when enabled
- Visual warning (screen flash, colour change) — separate ticket if wanted
- Warning during break countdown

## Risks

- **Tick frequency.** The repaint loop runs ~5 Hz. A threshold crossing between two ticks is still
  caught on the next tick (remaining is clock-derived, `INV-CD-1`), so the beep may be up to 200 ms
  late — imperceptible.
- **Paused crossing.** If the artist pauses at 10.1 s, paused time does not count (`INV-CD-2`), so
  remaining is still > 10 on resume. If they pause at 9.9 s the crossing already happened and the
  beep already played. No edge-case gap.

## Open questions

- Should the threshold be user-configurable (a number field) or a simple on/off toggle fixed at
  10 s? — decided by user
- Should the warning tone differ from the transition chime by pitch, by pattern (double beep), or
  both? — decided by implementation
