# FD-020 — Comment diet: `DrawingSession<TImage>`

Status: ready-for-agent

**Story:** _As the next person to read the session aggregate, I open 744 lines and find code, not a 218-line retelling of the domain model beside it._
**Depends on:** FD-016 (the keep-list), FD-017, FD-018, FD-019, FD-021, FD-022, FD-023, FD-024

## Summary

`FigureDrawing.Core/Session/DrawingSession.cs` is 218 comment lines in 744 — the largest single
block of prose in the repo, and the file with the most rules that may exist only as comments. It
hosts catalogue object 7. Apply the [FD-016 §2](FD-016-comment-diet.md) keep-list. Deliberately
late in the sequence: by the time this lands, the keep-list has been exercised on seven files.

## Model placement

| | |
|---|---|
| Context | Session Execution |
| Owning object | `DrawingSession<TImage>` (and the non-generic `DrawingSession` partner, the `SessionPhase` / `PauseReason` / tick-result enums in the same file) |
| New Core type | no — no type is added, split or moved |
| New invariants | none in code. An `INV-SES-*`, `INV-CD-*`, `INV-PLY-*`, `INV-SUM-*` or `INV-POSE-*` rule living only in a comment moves into [DOMAIN-MODEL.md §4.1](../DOMAIN-MODEL.md) in this PR |
| Invariants changed | none |
| Crosses a boundary | no |

## Approach

- **Core:** `Session/DrawingSession.cs` only.
  - The file-header block on the aggregate — what it owns, and the six types it was merged from —
    is DOMAIN-MODEL.md §4.1 and §9 restated. Delete it (FD-016 §2.1.3) after confirming §9 still
    names all six.
  - The `// --- Run state: ... ---` banners go (§2.1.2).
  - Enum-member prose on `SessionPhase`, `PauseReason` and the tick result goes where the member
    name carries it (§2.1.5); the tick-result distinction that FD-011 introduced (`INV-SES-13`)
    stays only if DOMAIN-MODEL.md does not already carry it.
  - The failure-budget rule that FD-010 made a function of session length is a prime §2.2.1
    candidate: keep two lines, cite the invariant, or move it to the doc.
- **Android:** none.
- **Resources:** none.
- **Persistence:** none.
- **Injected dependency:** none — the clock and `Random` injection points are unchanged.

## Acceptance criteria

- [ ] `DrawingSession.cs` is at or under 5% comment lines (≤ 26 of ~530 remaining lines), no
      comment block over two lines
- [ ] The comment-stripped file is byte-identical before and after, except FD-016 §2.3 renames
- [ ] The public surface is unchanged: same members, same names, same signatures, same order
- [ ] Every deleted rule is findable afterwards in DOMAIN-MODEL.md §4.1/§9 or ARCHITECTURE.md —
      listed rule by rule in the PR description
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count
- [ ] The seven `DrawingSession*` test files, `SessionE2ETests` and `SessionScreenContractTests`
      are unedited

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `DrawingSessionTests`, `DrawingSessionBreakTests`, `DrawingSessionCountdownTests`, `DrawingSessionImageTests`, `DrawingSessionSetupTests`, `DrawingSessionTimeAccountingTests` — run, not edited |
| Contract | `SessionScreenContractTests` — run, not edited |
| E2E-model | `SessionE2ETests` — run, not edited; a whole session to its summary |
| UI (Appium) | `n/a` — no behaviour changes, so no device run is required |

## Out of scope

- `ViewerTools` → FD-021
- `SessionActivity`'s repaint loop and lifecycle wiring → FD-026
- Splitting the file, reordering members, or extracting any of the six merged concepts back out.
  A comment PR that also moves code cannot be reviewed as a comment PR.

## Risks

- Highest knowledge-loss risk in FD-016. Mitigation: this ticket lands eighth, not first, and its
  PR description enumerates every deleted rule with the doc section that now holds it.
- The file mixes five invariant families. Losing one family's rule is easy to miss in review —
  check the families off one at a time against DOMAIN-MODEL.md §4.1.

## Open questions

- none — FD-017 settled the type-header question
