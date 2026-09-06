# FD-027 — Comment diet: the test suites

Status: needs-triage

**Story:** _As the next person to read a test, I find one line saying why the rule exists and then the test, with no step-by-step narration in between._
**Depends on:** FD-016 (the keep-list), FD-017 … FD-026 (the suites are their safety net and stay untouched until they land)

## Summary

`FigureDrawing.Tests` carries 1,111 comment lines and `FigureDrawing.UITests` 317. Most of that is
mandated: [CLAUDE.md](../../CLAUDE.md) requires a comment above each test saying *why the rule
exists*, citing its invariant id, and [FD-016 §2.2.4](FD-016-comment-diet.md) keeps every one of
them. This ticket is therefore deliberately narrow: only narration *inside* test bodies goes —
`// Arrange`, `// Act`, `// Assert`, and line-by-line retellings of what the next statement does.

`needs-triage` rather than `ready-for-agent`: the boundary between "why the rule exists" and
"narration" is a judgement the user should confirm on a sample before an agent applies it to 28
files.

## Model placement

| | |
|---|---|
| Context | all four — the suites cover every context |
| Owning object | none |
| New Core type | no |
| New invariants | none |
| Invariants changed | none |
| Crosses a boundary | no |

## Approach

- **Tests:** every file in `FigureDrawing.Tests` and `FigureDrawing.UITests`.
  - Keep: the why-comment above each `[Fact]`/`[Theory]`, with its invariant id. Untouched, word
    for word.
  - Keep: `#pragma warning disable` justifications (`SettingsTests.cs:385`).
  - Keep: the CRLF/`$`-anchoring note in `SourceContract.cs` and the harness notes in
    `AppiumGuard.cs` and `UiTestEnvironment.cs` — §2.2.2 platform traps, and the emulator harness is
    exactly the place a reader cannot infer them.
  - Delete: arrange/act/assert banners, restatements of the next line, and prose duplicating the
    why-comment already above the test.
- **Core:** none.
- **Android:** none.

## Acceptance criteria

- [ ] Every test still has its why-comment, with the invariant id it had before — verified by
      diffing the set of comment lines directly above test attributes, before and after
- [ ] No arrange/act/assert banner remains in either project
- [ ] The comment-stripped source of every touched file is byte-identical before and after
- [ ] `SourceContract.cs`'s CRLF note and `SettingsTests.cs`'s `#pragma` justification are intact
- [ ] `AppiumGuard.cs` and `UiTestEnvironment.cs` keep their harness traps (stale server on :4723,
      leftover app state, one session per device)
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count and same test names
- [ ] The Appium tier still runs via `scripts/run-appium-tests.ps1` on at least one AVD

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | the suite is the subject; it must run green with an unchanged test list |
| Contract | `SourceContractTests` — it tests `SourceContract`, which this PR edits; run it, do not weaken it |
| E2E-model | `SessionE2ETests` — run |
| UI (Appium) | `scripts/run-appium-tests.ps1` — one full run, since `AppiumGuard` and `UiTestEnvironment` are edited |

## Out of scope

- The CLAUDE.md why-comment rule itself. It stands. This ticket does not propose relaxing it.
- Renaming tests, merging tests, changing assertions, or deleting a test
- Reordering or reformatting test bodies

## Risks

- The CLAUDE.md rule and "remove almost all comments" pull against each other here. The rule wins:
  a test's why-comment is the only record of what the test is for, and the suite is what makes
  FD-017…FD-026 safe. If this ticket ever looks like it is trimming those, stop it.
- Editing `SourceContract.cs` risks the contract tier itself. `SourceContractTests` must be run and
  must not be edited in the same PR.

## Open questions

- Where exactly is the line between "why the rule exists" and "narration"? — decided by user, on a
  sample of three files, before the rest proceed
- Is this worth doing at all, given §2.2.4 keeps the bulk of it? — decided by user
