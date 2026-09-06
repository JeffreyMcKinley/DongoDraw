# FD-028 — The comment diet holds

Status: needs-triage

**Story:** _As the person who paid for eleven pull requests of comment removal, I get a red build when the prose starts growing back._
**Depends on:** FD-016 … FD-026 (there must be a diet before there is a guard), optionally FD-027

## Summary

A contract test that fails when a production source file exceeds a comment-density budget. Without
it, FD-016's eleven removal PRs decay one helpful paragraph at a time, and the next person to read
`SessionActivity.cs` is back where this started.

`needs-triage`: this adds a rule that will one day block a change someone wants, and whether that
trade is worth making is the user's call, not an agent's. Decide it after FD-026 lands, when the
real post-diet numbers are known.

## Model placement

| | |
|---|---|
| Context | none — a repo-level guard, like `AndroidBuildTests` and `VersionTests` |
| Owning object | none |
| New Core type | no |
| New invariants | one enforcement rule, in [DOMAIN-MODEL.md §8](../DOMAIN-MODEL.md) terms: production source stays under the comment budget |
| Invariants changed | none |
| Crosses a boundary | no |

## Approach

- **Tests:** a new `CommentBudgetTests` in `FigureDrawing.Tests`, in the contract tier — it reads
  files rather than running them, like `AndroidBuildTests`.
  - Locate the repo root with `TestPaths`, the same way the other contract tests do.
  - Enumerate production `.cs` files: the four project trees, excluding `obj/`, `bin/`,
    `.claude/worktrees/`, generated files, and both test projects.
  - Assert per file: comment lines ÷ total lines ≤ the budget, and no comment block longer than two
    lines.
  - Budget: 5%, matching FD-016 §7. Confirm against the real numbers after FD-026 and set it there.
  - The failure message names the file, its ratio and the budget — a guard nobody can read is a
    guard people delete.
- **Core:** none.
- **Android:** none.

## Acceptance criteria

- [ ] The test fails today, before the budget is applied — write it, run it red against a file with
      a paragraph re-added, then let the diet make it green (TDD, per CLAUDE.md)
- [ ] Every production file passes after FD-026 with no exemption list
- [ ] Test projects are excluded, so the CLAUDE.md why-comment rule cannot fail the build
- [ ] `obj/`, `bin/`, `.claude/worktrees/` and generated files are excluded
- [ ] A file added tomorrow is covered without editing the test
- [ ] The failure message names the file, its ratio and the budget
- [ ] `./nx.bat run FigureDrawing.Tests:test` green

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `n/a` |
| Contract | `CommentBudgetTests` — new. Its own ratio maths is covered over a fixture string, the way `SourceContractTests` covers `SourceContract` |
| E2E-model | `n/a` |
| UI (Appium) | `n/a` |

## Out of scope

- Enforcing anything about comment *quality*, or that a kept comment cites an invariant id. A test
  cannot judge that; review can.
- A pre-commit hook or CI-only check. This is a test, in the tier that already reads source.
- Applying the budget to `docs/`, `.axml`, or build files

## Risks

- A density cap punishes the one file that genuinely needs a kept explanation. Mitigation: the
  budget is per file and 5% of a 900-line file is 45 lines — enough for every §2.2 keeper.
- A guard that fires on legitimate work gets suppressed rather than obeyed. If the first three
  months produce more suppressions than catches, delete the test; that is a real outcome, not a
  failure.

## Open questions

- Is the guard worth its future friction? — decided by user, after FD-026
- 5%, or the real post-FD-026 maximum plus headroom? — decided by user, from the measured numbers
