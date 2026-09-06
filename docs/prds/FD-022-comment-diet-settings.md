# FD-022 — Comment diet: `Settings`

Status: ready-for-agent

**Story:** _As the next person to read `Settings.cs`, I see what is persisted and how, not 63 lines of prose about LiteDB._
**Depends on:** FD-016 (the keep-list), FD-017 (§2.2.5 precedent)

## Summary

`FigureDrawing.Core/Data/Settings.cs` is 63 comment lines in 238. It hosts catalogue object 9,
`Settings`, the Preferences aggregate root over the on-device LiteDB store. Apply the
[FD-016 §2](FD-016-comment-diet.md) keep-list.

## Model placement

| | |
|---|---|
| Context | Preferences |
| Owning object | `Settings` |
| New Core type | no |
| New invariants | none in code. An `INV-SET-P*` or `INV-STO-*` rule living only here moves into [DOMAIN-MODEL.md §5.1](../DOMAIN-MODEL.md) in this PR |
| Invariants changed | none |
| Crosses a boundary | no — no property is added, renamed, retyped or given a different default |

## Approach

- **Core:** `Data/Settings.cs` only. Delete under FD-016 §2.1, keep under §2.2, rename under §2.3.
  Concurrency and write-ordering notes are §2.2.1 candidates: a plausible edit re-breaks them and
  the compiler will not catch it — keep two lines with the invariant id, or move the rule to
  DOMAIN-MODEL.md §5.1.
- **Android:** none.
- **Resources:** none.
- **Persistence:** none changed. Property names are the persisted schema; a rename under FD-016
  §2.3 must not touch any property that LiteDB serialises.
- **Injected dependency:** none.

## Acceptance criteria

- [ ] `Settings.cs` is at or under 5% comment lines (≤ 9 of ~175 remaining lines), no comment block
      over two lines
- [ ] The comment-stripped file is byte-identical before and after, except FD-016 §2.3 renames
- [ ] No persisted property name, type or default changes — a settings document written by the
      previous build still loads
- [ ] Every deleted rule is findable afterwards in DOMAIN-MODEL.md §5.1 or ARCHITECTURE.md
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count
- [ ] `SettingsTests` is unedited, including the `#pragma warning disable xUnit1031` justification
      at `SettingsTests.cs:385` (FD-016 §2.2.3 keeps it, and FD-027 does not touch it either)

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `SettingsTests` — run, not edited (`INV-SET-P*`, `INV-STO-*`, including the concurrent-write case) |
| Contract | `FolderMemoryContractTests` — run, not edited |
| E2E-model | `n/a` |
| UI (Appium) | `n/a` |

## Out of scope

- The remembered-folder rules that live in `LibraryReference` → FD-018
- Where the settings file lives on device, and its migration story
- Any change to defaults, even one a comment calls wrong

## Risks

- Property names are schema. A "clarifying" rename here is a silent data loss on the next launch —
  FD-016 §2.3 renames are limited to locals and private members that LiteDB never sees.

## Open questions

- none
