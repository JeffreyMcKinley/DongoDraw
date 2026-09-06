# FD-026 — Comment diet: `SessionActivity`

Status: ready-for-agent

**Story:** _As the next person to read the player screen, I see the repaint loop and the lifecycle calls, not 305 lines of prose spread through 1,210._
**Depends on:** FD-016 (the keep-list), FD-020, FD-021, FD-023 (the Core it wires)

## Summary

`SessionActivity.cs` is 305 comment lines in 1,210, including eight trailing comments — the largest
comment count in the repo. It is the player screen: repaint loop, tick handling, break overlay,
viewer tools, grid overlay, foldable layout. Apply the [FD-016 §2](FD-016-comment-diet.md)
keep-list. Lands after FD-020, so the session rules it echoes are already confirmed in the docs.

## Model placement

| | |
|---|---|
| Context | Session Execution (Android layer only) |
| Owning object | none — the screen wires `DrawingSession<TImage>`, `ViewerTools` and the rendering services to views |
| New Core type | no |
| New invariants | none. A session rule found only here is a finding: it belongs in Core (`INV-SES-13` is the precedent, from FD-011). Record it and open a follow-up; do not move code in this PR |
| Invariants changed | none |
| Crosses a boundary | no — no intent extra, no view id, no string |

## Approach

- **Android:** `SessionActivity.cs` only, including the eight trailing comments.
  - Bitmap lifetime and recycling notes are §2.2.2 platform traps and stay.
  - The handoff bound FD-010 introduced — a pose boundary never blocks the repaint loop — stays
    only as a two-line note citing `INV-PLY-7`/`INV-PLY-8`, if DOMAIN-MODEL.md does not already
    carry it (FD-020 will have checked).
  - Foldable posture and configuration-change notes stay where a plausible edit re-breaks them.
  - Prose restating what `Tick()` returns, what a phase means, or what the session counts goes:
    that is DOMAIN-MODEL.md §4.1.
- **Core:** none.
- **Resources:** none.
- **Persistence:** none.
- **Injected dependency:** none.

## Acceptance criteria

- [ ] `SessionActivity.cs` is at or under 5% comment lines (≤ 45 of ~905 remaining lines), no
      comment block over two lines
- [ ] The comment-stripped file is byte-identical before and after, except FD-016 §2.3 renames
- [ ] Bitmap lifetime/recycling notes are kept (FD-016 §2.2.2)
- [ ] The FD-010 handoff rule is still stated somewhere — a kept two-line comment or
      DOMAIN-MODEL.md
- [ ] No method named by `SessionScreenContractTests` or `CrossActivityContractTests` is renamed
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count
- [ ] `SessionScreenContractTests`, `CrossActivityContractTests` and `UiResourceContractTests` are
      unedited

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `n/a` |
| Contract | `SessionScreenContractTests`, `CrossActivityContractTests`, `UiResourceContractTests`, `TypefaceContractTests` — run, not edited |
| E2E-model | `SessionE2ETests` — run, not edited; its `Screen` harness mirrors this file's repaint loop |
| UI (Appium) | `SessionPlayerUiTests` — optional. Run it if any §2.3 rename touched the repaint or lifecycle path |

## Out of scope

- `DrawingSession<TImage>` → FD-020
- `ViewerTools` → FD-021, rendering services → FD-023
- Moving any rule out of the screen and into Core, however obviously it belongs there. Note it,
  ticket it, leave it.
- Any change to the repaint cadence, decode timing, or bitmap recycling

## Risks

- Largest file and largest comment count: reviewer fatigue is the real risk. Land it in ordered
  commits — one per region of the file — so each diff stays readable.
- `SessionScreenContractTests` pins wiring between this screen's own methods. Rename nothing it
  names.

## Open questions

- none
