# FD-011 — The session says what changed, not just that something did

Status: shipped

> **Shipped**, ahead of FD-010 as this ticket's out-of-scope section asked. Nine assertions became
> enum comparisons and no scenario changed; the four chime tests in `SessionE2ETests` passed
> untouched, which is the check that this changed no behaviour. `Completed` is reported for a tick
> only — `End()` still returns nothing, the narrower of the two readings in Open questions.

**Story:** _As an artist, I want the change-of-pose tone to mean a new pose — not a rest starting,
and not the session ending._
**Depends on:** FD-003, FD-005

## Summary

`DrawingSession.Tick()` returns a `bool`: something changed, work out what. The player screen has to
reconstruct the transition from the state left behind (`if (!session.OnBreak) Chime();`), which puts
a rule — *what counts as a new pose* — in an Activity where no unit test can reach it. After this the
session reports which transition it made, the screen plays a tone when told to, and the rule is
covered where it belongs.

The behaviour is already correct on screen; this is the rule moving to its home. Deferred out of the
2026-08-16 review because it rewrites nine existing assertions and that suite change wanted its own
ticket.

## Model placement

| | |
|---|---|
| Context | Session Execution |
| Owning object | `DrawingSession<TImage>` |
| New Core type | yes, one enum — `SessionTick` (`None`, `PoseStarted`, `BreakStarted`, `Completed`). It is a return value, not a concept with a lifetime, so it does not add a tenth domain object (DOMAIN-MODEL.md §9). |
| New invariants | `INV-SES-13` — **A tick reports the transition it made.** `Tick()` returns exactly one of: nothing happened, a pose started, a rest started, the session completed. A caller never has to infer the transition from the state afterwards. |
| Invariants changed | none in meaning. `INV-SES-10` and `INV-CD-6` are what the enum makes observable; neither rule changes. |
| Crosses a boundary | no. |

## Approach

- **Core:** `Tick()` returns `SessionTick` instead of `bool`. The state machine already knows which
  branch it took — `StartPose`, the break branch of `CompletePose`, and `Finish` each map to one
  value — so this is a return type, not new logic. Keep the "no phase change" case as the enum's
  default so a missed `switch` arm reads as "nothing happened".
- **Android:** `SessionActivity.Tick` switches on the result: chime on `PoseStarted`, render on
  anything but `None`. The `!session.OnBreak` reconstruction and the `IsComplete` guard inside
  `Chime()` both disappear — completion is its own case now.
- **Resources:** none.
- **Persistence:** none.
- **Injected dependency:** none.

## Acceptance criteria

- [x] A pose expiring with no break configured reports `PoseStarted`; the same expiry with a break
      configured reports `BreakStarted`, and the break's own expiry reports `PoseStarted`.
- [x] The tick that reaches the configured count reports `Completed`, whether or not a break is
      configured, and never `PoseStarted`.
- [x] A tick on a draft, a complete, a paused, or an unexpired session reports `None` and changes
      nothing.
- [x] The screen chimes once per boundary with a break configured, not twice, and does not chime on
      the tick that completes the session.
- [x] `CompletedCount`, `SkippedCount` and `TotalDrawingTime` are unchanged by this ticket at every
      point — the transition is reported, not redefined.
- [x] `Next`, `Skip` and `End` keep their current signatures: a manual command is not a tick and
      still does not chime.

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `DrawingSessionBreakTests` — the transition table above, `INV-SES-13`. **This is the suite change:** nine assertions currently read `Tick()` as a bool and must become enum comparisons — `DrawingSessionBreakTests.cs:83, 101, 116, 164, 194, 355`, `DrawingSessionCountdownTests.cs:224, 381`, `DrawingSessionSetupTests.cs:155`. Each is a one-line edit; none of the scenarios change. |
| Contract | `n/a`. |
| E2E-model | `SessionE2ETests` — the `Screen` harness switches on the result instead of reconstructing it. Its four chime tests (`WithBreaks_OnlyTheBreaksExitChimes`, `WithoutBreaks_EveryPoseChangeChimes_ExceptTheLast`, `AManualAdvance_DoesNotChime`, and the break-timing test) must pass unchanged — that is the check that this ticket changed no behaviour. |
| UI (Appium) | `n/a` — audible output is not asserted on a device. |

## Out of scope

- Any change to *when* the tone plays. This ticket relocates the decision; FD-012 territory if the
  policy itself is ever revisited (a tone at a break's start, a distinct end-of-session tone).
- Chime volume, tone selection, or a per-session mute — all `Settings`/`ToneGenerator` concerns.
- Prefetching at the boundary (FD-010), which touches the same `Tick` call site. If both land, do
  FD-011 first: FD-010's screen-side code reads better against an enum than against a bool.

## Risks

- Nine test edits across three files, all mechanical. Doing them in the same commit as the Core
  change is what keeps the suite bisectable.
- A `bool` → enum return is a source-breaking change to a public Core API. Nothing outside this repo
  consumes it, but the `Tick()` call in `SessionActivity` and the one in the E2E harness must move
  together.

## Open questions

- Should `Completed` be reported for a session that ends via `End()` as well, or does the enum only
  describe what a *tick* did? — decided by implementation; the narrower reading (ticks only) is the
  one this ticket assumes.
