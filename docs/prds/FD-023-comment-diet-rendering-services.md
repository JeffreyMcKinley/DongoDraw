# FD-023 — Comment diet: the rendering services

Status: ready-for-agent

**Story:** _As the next person to read the contrast and bitmap maths, I see the arithmetic, not an essay about where the file belongs._
**Depends on:** FD-016 (the keep-list), FD-017 (§2.2.5 precedent)

## Summary

Four supporting rendering services ([ARCHITECTURE.md §16](../ARCHITECTURE.md)) carry 93 comment
lines in 329: `GridContrast.cs` (53 / 201), `PoseImage.cs` (15 / 38), `BitmapMath.cs` (13 / 45),
`ImageDecoding.cs` (12 / 45). None is a catalogue object; they are grouped into one PR because they
are one kind of thing and each is too small to be its own. Apply the
[FD-016 §2](FD-016-comment-diet.md) keep-list.

## Model placement

| | |
|---|---|
| Context | Rendering |
| Owning object | none — supporting services beside `DrawingSession` and `ViewerTools`, ARCHITECTURE.md §16 |
| New Core type | no |
| New invariants | none. `GridContrast`'s totality contract (every degenerate input returns four light styles rather than throwing) is a rule with no invariant id — if ARCHITECTURE.md does not carry it, this PR adds it there |
| Invariants changed | none |
| Crosses a boundary | no |

## Approach

- **Core:** `GridContrast.cs`, `BitmapMath.cs`. The placement essay in `GridContrast` ("it sits
  beside `BitmapMath` rather than in `Session/` because…") is ARCHITECTURE.md §16 and goes under
  FD-016 §2.1.3. The tuning constants (`SampleGrid`, `LightThreshold`, `BandHalfWidth`) carry
  chosen numbers with a reason no name can hold — §2.2.1 keeps one line each, at most.
- **Android:** `PoseImage.cs`, `ImageDecoding.cs`. Bitmap ownership and recycling notes are §2.2.2
  platform traps and stay.
- **Resources:** none.
- **Persistence:** none.
- **Injected dependency:** none.

## Acceptance criteria

- [ ] Each of the four files is at or under 5% comment lines, no comment block over two lines
- [ ] Each comment-stripped file is byte-identical before and after, except FD-016 §2.3 renames
- [ ] The three tuning constants keep a one-line reason for their value, or that reason is in
      ARCHITECTURE.md
- [ ] `GridContrast`'s totality contract survives in the docs or in a kept two-line comment
- [ ] Bitmap recycling and ownership notes in `PoseImage`/`ImageDecoding` are kept (FD-016 §2.2.2)
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count
- [ ] `GridContrastTests` and `BitmapMathTests` are unedited

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `GridContrastTests`, `BitmapMathTests` — run, not edited |
| Contract | `TypefaceContractTests`, `UiResourceContractTests` — run, not edited |
| E2E-model | `n/a` |
| UI (Appium) | `n/a` — no rendering change |

## Out of scope

- Retuning any constant. If a value looks wrong, that is a separate ticket with a test.
- `SessionActivity`'s use of these services → FD-026
- `ViewerTools` → FD-021

## Risks

- These files run on the render path, where a wrong "clarification" is a dropped frame or a crash.
  Nothing but comments changes here.

## Open questions

- none
