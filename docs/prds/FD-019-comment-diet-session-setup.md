# FD-019 — Comment diet: `SessionSetup` and `SessionConfig`

Status: ready-for-agent

**Story:** _As the next person to read `SessionSetup.cs`, I see the Start gate and the config it produces, not 39 lines of prose over 44 lines of code._
**Depends on:** FD-016 (the keep-list), FD-017 (§2.2.5 precedent)

## Summary

`FigureDrawing.Core/SessionSetup.cs` is 39 comment lines in 83 — the worst ratio in the repo at
47%. It hosts catalogue objects 5 and 6, `SessionSetup` and `SessionConfig`. Apply the
[FD-016 §2](FD-016-comment-diet.md) keep-list.

## Model placement

| | |
|---|---|
| Context | Session Setup |
| Owning object | `SessionSetup`, `SessionConfig` |
| New Core type | no |
| New invariants | none in code. An `INV-SET-1..5` or `INV-CFG-*` rule living only here moves into [DOMAIN-MODEL.md §3.1/§3.2](../DOMAIN-MODEL.md) in this PR |
| Invariants changed | none |
| Crosses a boundary | no — `SessionConfig` is a contract between Setup and Execution, and neither its fields nor their order change |

## Approach

- **Core:** `SessionSetup.cs` only. Delete under FD-016 §2.1, keep under §2.2, rename under §2.3.
- **Android:** none.
- **Resources:** none.
- **Persistence:** none.
- **Injected dependency:** none.

## Acceptance criteria

- [ ] `SessionSetup.cs` is at or under 5% comment lines (≤ 4 of ~44 remaining lines), no comment
      block over two lines
- [ ] The comment-stripped file is byte-identical before and after, except FD-016 §2.3 renames
- [ ] `SessionConfig`'s field names, types and order are unchanged — it crosses an intent boundary
- [ ] Every deleted rule is findable afterwards in DOMAIN-MODEL.md §3.1/§3.2 or ARCHITECTURE.md
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count
- [ ] `SessionSetupTests` and `DrawingSessionSetupTests` are unedited

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `SessionSetupTests`, `DrawingSessionSetupTests` — run, not edited (`INV-SET-1..5`, `INV-CFG-*`) |
| Contract | `n/a` |
| E2E-model | `SessionE2ETests` — run, not edited; it builds a config from a draft |
| UI (Appium) | `n/a` |

## Out of scope

- The setup screen's wiring in `MainActivity` → FD-025
- The length estimate's formatting helper on the non-generic `DrawingSession` → FD-020
- Any change to which drafts pass the Start gate

## Risks

- At 47% comments this file is where "the comment *is* the spec" is most likely true. Grep each
  rule against DOMAIN-MODEL.md §3 before deleting it.

## Open questions

- none
