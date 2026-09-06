# FD-021 — Comment diet: `ViewerTools`

Status: ready-for-agent

**Story:** _As the next person to read `ViewerTools.cs`, I see 37 lines of zoom, flip and grid state instead of 21 lines of prose over them._
**Depends on:** FD-016 (the keep-list), FD-017 (§2.2.5 precedent)

## Summary

`FigureDrawing.Core/Session/ViewerTools.cs` is 21 comment lines in 58. It hosts catalogue object 8.
Apply the [FD-016 §2](FD-016-comment-diet.md) keep-list. Small file, so it is a cheap early
exercise of the keep-list on Session Execution before FD-020 takes on the aggregate.

## Model placement

| | |
|---|---|
| Context | Session Execution |
| Owning object | `ViewerTools` |
| New Core type | no |
| New invariants | none in code. An `INV-VIEW-*` rule living only here moves into [DOMAIN-MODEL.md §4.2](../DOMAIN-MODEL.md) in this PR |
| Invariants changed | none |
| Crosses a boundary | no |

## Approach

- **Core:** `Session/ViewerTools.cs` only. Delete under FD-016 §2.1, keep under §2.2, rename under
  §2.3. `INV-VIEW-3` — the entity holds no bitmap, no matrix and no view — is already in
  DOMAIN-MODEL.md; the comment restating it goes.
- **Android:** none.
- **Resources:** none.
- **Persistence:** none.
- **Injected dependency:** none.

## Acceptance criteria

- [ ] `ViewerTools.cs` is at or under 5% comment lines (≤ 2 of ~37 remaining lines)
- [ ] The comment-stripped file is byte-identical before and after, except FD-016 §2.3 renames
- [ ] Every deleted rule is findable afterwards in DOMAIN-MODEL.md §4.2 or ARCHITECTURE.md
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count
- [ ] `ViewerToolsTests` is unedited

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `ViewerToolsTests` — run, not edited (`INV-VIEW-*`) |
| Contract | `SessionScreenContractTests` — run, not edited |
| E2E-model | `n/a` |
| UI (Appium) | `n/a` |

## Out of scope

- `GridContrast` (grid *colour*, a rendering service) → FD-023
- The overlay's rendering in `SessionActivity` → FD-026

## Risks

- Small file, low risk. The one thing to preserve is why grid colour is not viewer state — that is
  `INV-VIEW-3` and it is already documented, so the comment goes.

## Open questions

- none
