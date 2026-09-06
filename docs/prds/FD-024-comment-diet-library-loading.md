# FD-024 — Comment diet: the library load machinery

Status: ready-for-agent

**Story:** _As the next person to read the background loader, I see the generation and state handling, not a prose retelling of FD-009._
**Depends on:** FD-016 (the keep-list), FD-017 (§2.2.5 precedent)

## Summary

The FD-009 background-load machinery carries 134 comment lines in 339: `LibraryLoader.cs`
(97 / 282, Android side), `LoadGeneration.cs` (20 / 32) and `LibraryLoadState.cs` (17 / 25), the
last two at over 60% comments. One PR, because the three files are one mechanism. Apply the
[FD-016 §2](FD-016-comment-diet.md) keep-list.

## Model placement

| | |
|---|---|
| Context | Reference Library (loading) |
| Owning object | `ReferenceLibrary`'s loading path — `LibraryLoader`, `LoadGeneration`, `LibraryLoadState` are the FD-009 mechanism, not catalogue objects |
| New Core type | no |
| New invariants | none in code. `INV-X-13` and the background-work shape are [ARCHITECTURE.md §7](../ARCHITECTURE.md#7-threading); a rule that lives only in a comment moves there in this PR |
| Invariants changed | none |
| Crosses a boundary | no |

## Approach

- **Core:** `LibraryLoadState.cs`, `LoadGeneration.cs`. Both are small types whose comments are
  mostly member prose (FD-016 §2.1.5). The stale-result rule — why a generation is compared before
  a result is applied — is the one thing here a plausible edit re-breaks: §2.2.1, two lines with
  `INV-X-13`, or move it to ARCHITECTURE.md §7.
- **Android:** `LibraryLoader.cs`. Thread-affinity and cancellation notes are §2.2.2 platform traps
  and stay, at two lines each.
- **Resources:** none.
- **Persistence:** none.
- **Injected dependency:** none — the loader's injection points are unchanged.

## Acceptance criteria

- [ ] Each of the three files is at or under 5% comment lines, no comment block over two lines
- [ ] Each comment-stripped file is byte-identical before and after, except FD-016 §2.3 renames
- [ ] The generation-comparison rule (`INV-X-13`) is findable afterwards in ARCHITECTURE.md §7 or
      kept as a two-line comment
- [ ] Thread-affinity and cancellation notes in `LibraryLoader` are kept (FD-016 §2.2.2)
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count
- [ ] `LibraryLoadContractTests`, `LibraryLoadStateTests` and `LoadGenerationTests` are unedited

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `LibraryLoadStateTests`, `LoadGenerationTests` — run, not edited |
| Contract | `LibraryLoadContractTests` — run, not edited; it reads `LibraryLoader` as a file, with comments already stripped |
| E2E-model | `n/a` |
| UI (Appium) | `FolderPickerUiTests` — not run for this PR; nothing observable changes |

## Out of scope

- `ReferenceLibrary.cs` itself → FD-017
- `MainActivity`'s call into the loader → FD-025
- The two FD-009 acceptance criteria still open pending manual device verification. This PR neither
  closes nor affects them.

## Risks

- `LibraryLoadContractTests` asserts on `LibraryLoader`'s *code*. It strips comments first
  (ARCHITECTURE.md §11), so removing them cannot change the result — but a §2.3 rename of a method
  the contract names would. Rename nothing the contract tests reference.

## Open questions

- none
