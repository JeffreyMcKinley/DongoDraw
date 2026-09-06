# FD-017 — Comment diet: `ReferenceLibrary` and `IDocumentTree`

Status: ready-for-agent

**Story:** _As the next person to read `ReferenceLibrary.cs`, I see 250 lines of pooling and grouping rules instead of 83 lines of prose wrapped around them._
**Depends on:** FD-016 (the keep-list)

## Summary

`FigureDrawing.Core/ReferenceLibrary.cs` is 83 comment lines in 250. It hosts two catalogue
objects — `ReferenceLibrary` (object 2) and `IDocumentTree`/`DocumentEntry` (object 3). Apply the
[FD-016 §2](FD-016-comment-diet.md) keep-list to the whole file. First child of FD-016, so it also
settles FD-016 §10's first open question: whether a one-line type header survives at all.

## Model placement

| | |
|---|---|
| Context | Reference Library |
| Owning object | `ReferenceLibrary`, `IDocumentTree` / `DocumentEntry` |
| New Core type | no |
| New invariants | none in code. If a comment states an `INV-GRP-*`, `INV-POOL-*` or `INV-TREE-*` rule that [DOMAIN-MODEL.md §2.2/§2.3](../DOMAIN-MODEL.md) does not, add it there in this PR |
| Invariants changed | none |
| Crosses a boundary | no |

## Approach

- **Core:** `ReferenceLibrary.cs` only. Delete under FD-016 §2.1, keep under §2.2, rename under
  §2.3. Every deleted rule is grepped against DOMAIN-MODEL.md and ARCHITECTURE.md first; a rule
  found nowhere is added to DOMAIN-MODEL.md as part of this PR.
- **Android:** none.
- **Resources:** none.
- **Persistence:** none.
- **Injected dependency:** none.

## Acceptance criteria

- [ ] `ReferenceLibrary.cs` is at or under 5% comment lines (≤ 13 of ~250), no comment block over
      two lines
- [ ] The comment-stripped file is byte-identical before and after, except for renames made under
      FD-016 §2.3
- [ ] Every rule removed with its comment is findable afterwards in DOMAIN-MODEL.md §2.2/§2.3 or
      ARCHITECTURE.md — cite where, per rule, in the PR description
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count as before
- [ ] `ReferenceLibraryTests` and `LibraryLoadContractTests` are unedited
- [ ] The PR records the §2.2.5 decision (bare types, or one-line headers) as the precedent
      FD-018…FD-026 follow

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `ReferenceLibraryTests` — run, not edited. `INV-GRP-*`, `INV-POOL-*`, `INV-TREE-*` must stay green |
| Contract | `LibraryLoadContractTests` — run, not edited. It strips comments before asserting, so it cannot see this change |
| E2E-model | `SessionE2ETests` — run, not edited |
| UI (Appium) | `n/a` — no behaviour reaches a screen |

## Out of scope

- `LibraryLoader.cs`, `LibraryLoadState.cs`, `LoadGeneration.cs` → FD-024
- `LibraryReference.cs` → FD-018
- Any change to pooling, grouping or enumeration behaviour

## Risks

- The file holds `INV-POOL-6` as narrowed by FD-010. If its narrowing lives only in a comment,
  delete nothing until it is in DOMAIN-MODEL.md.

## Open questions

- Does a one-line type header survive (FD-016 §2.2.5)? — decided by user on this PR, then binding
  on the rest
