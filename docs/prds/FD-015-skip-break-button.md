# FD-015 — Skip break button

Status: ready-for-agent

**Story:** _As an artist, I can tap "Ready" during a break to start the next pose immediately, so I am not forced to wait when I am already prepared._
**Depends on:** none

## Summary

When a break is active between poses, a "Ready" button appears below the break timer. Tapping it
ends the break immediately and starts the next pose. The Core already supports this — `Next()`
during a break advances to the next pose — so this ticket is Android wiring and UI only.

## Model placement

| | |
|---|---|
| Context | Session Execution (Android layer only) |
| Owning object | `DrawingSession<TImage>` — `Next()` already handles break-to-pose transition |
| New Core type | no |
| New invariants | none — `Next()` during break is already legal and tested |
| Invariants changed | none |
| Crosses a boundary | no |

## Approach

- **Core:** no changes. `Next()` called during `Phase == Break` already ends the break and starts
  the next pose with a fresh clock (`INV-CD-6`). `Tick()` returns `PoseStarted` on the next call.
  The transition chime fires if `ChimeOnChange` is on, same as an auto-advance.
- **Android:** `SessionActivity` — add a `Button` (or `MaterialButton`) to the break overlay
  layout. Visible only when `session.OnBreak` is true; hidden on pose start. `OnClick` calls
  `session.Next()` then runs the same post-tick UI update path as the timer expiry. The button
  text is the string resource `break_skip_label` ("Ready").
- **Resources:** `break_skip_label` string. Style reuses the existing Nocturne button tokens
  (`.btn-secondary` or equivalent from `styles.xml`). Placed below the break timer `TextView` in
  the break overlay `FrameLayout`.
- **Persistence:** none — no setting needed; the button is always available during a break. An
  artist who does not want to skip simply does not tap it.
- **Injected dependency:** none.

## Acceptance criteria

- [ ] During a break, a "Ready" button is visible below the break countdown
- [ ] Tapping the button ends the break and the next pose begins immediately with a full clock
      (`INV-CD-6`, `INV-POSE-2`)
- [ ] The button disappears when the pose starts (whether by tap or by timer expiry)
- [ ] The button is not visible during a pose phase or after session completion
- [ ] The transition chime plays on the skip if `ChimeOnChange` is on — same as a timer-driven
      advance
- [ ] The break skip does not count as a pose skip — `SkippedCount` is unchanged
- [ ] `CompletedCount`, `TotalDrawingTime` — unchanged (the previous pose was already counted by
      `Next()` before the break started)
- [ ] Foldable: the button renders correctly in both compact and tabletop postures

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `n/a` — `Next()` during break is already covered by `DrawingSessionBreakTests` |
| Contract | `UiResourceContractTests` — `break_skip_label` string exists |
| E2E-model | `n/a` |
| UI (Appium) | `BreakSkipUiTests` — button visible during break, tapping it starts next pose, button hidden during pose. Optional: only if break-driven UI tests are feasible on emulator timing |

## Out of scope

- Auto-skip break (zero-break override mid-session) — artist must tap
- Configurable break duration mid-session — break is set at session start (`INV-CFG-1`)
- "Extend break" button — separate ticket if wanted

## Risks

- **Double tap.** `Next()` on a completed session is a no-op (`INV-SES-6`), and `Next()` on a
  running pose advances normally. A rapid double-tap where the first ends the break and the second
  hits the now-running pose would skip that pose. Mitigation: disable the button in `OnClick`
  before calling `Next()`, or hide it immediately so a second tap has no target.
- **Break too short to tap.** A 1-second break may expire before the artist can reach the button.
  That is fine — the button is a convenience, not a guarantee. No minimum break duration is
  imposed.
