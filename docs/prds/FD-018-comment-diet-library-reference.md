# FD-018 — Comment diet: `LibraryReference`

Status: ready-for-agent

**Story:** _As the next person to read `LibraryReference.cs`, I see the grant rules themselves rather than a prose retelling of FD-012 and FD-013._
**Depends on:** FD-016 (the keep-list), FD-017 (sets the §2.2.5 precedent)

## Summary

`FigureDrawing.Core/LibraryReference.cs` is 73 comment lines in 208, including two trailing
comments. It hosts catalogue object 4, `LibraryReference`/`PersistedGrant`. Apply the
[FD-016 §2](FD-016-comment-diet.md) keep-list.

## Model placement

| | |
|---|---|
| Context | Reference Library |
| Owning object | `LibraryReference` / `PersistedGrant` |
| New Core type | no |
| New invariants | none in code. An `INV-REF-*` rule that lives only here moves into [DOMAIN-MODEL.md §2.4](../DOMAIN-MODEL.md) in this PR |
| Invariants changed | none |
| Crosses a boundary | no |

## Approach

- **Core:** `LibraryReference.cs` only, including the two trailing comments. Delete under FD-016
  §2.1, keep under §2.2, rename under §2.3.
- **Android:** none.
- **Resources:** none.
- **Persistence:** none — the persisted shape of a grant is untouched.
- **Injected dependency:** none.

## Acceptance criteria

- [ ] `LibraryReference.cs` is at or under 5% comment lines (≤ 10 of ~208), no comment block over
      two lines
- [ ] The comment-stripped file is byte-identical before and after, except FD-016 §2.3 renames
- [ ] A write-only grant, an expired grant and a grant to a vanished tree still behave as
      `INV-REF-*` says — by the existing tests, unedited
- [ ] Every deleted rule is findable afterwards in DOMAIN-MODEL.md §2.4 or ARCHITECTURE.md
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count
- [ ] `LibraryReferenceTests`, `FolderMemoryContractTests` and `CrossActivityContractTests` are
      unedited

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `LibraryReferenceTests` — run, not edited (`INV-REF-*`) |
| Contract | `FolderMemoryContractTests`, `CrossActivityContractTests` — run, not edited |
| E2E-model | `n/a` |
| UI (Appium) | `n/a` |

## Out of scope

- `MainActivity`'s side of folder memory → FD-025
- `Settings` persistence of the grant → FD-022
- Any change to grant validation or restore ordering — FD-013 fixed that ordering and this PR must
  not move it

## Risks

- FD-013 was a regression in *when* the save fires. If the ordering rule survives only as a comment
  here, put it in DOMAIN-MODEL.md before deleting it.

## Open questions

- none
