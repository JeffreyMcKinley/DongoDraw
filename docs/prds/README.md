# PRDs — FigureDrawing

Requirements docs for planned work, plus shipped tickets kept as the record of how a decision was
made and where it departed from the plan. One file per unit of work, written
against [ARCHITECTURE.md](../ARCHITECTURE.md) and [DOMAIN-MODEL.md](../DOMAIN-MODEL.md) — a PRD that
contradicts them is wrong, not visionary. Written with the `prd-generator` skill; the templates live
in `.claude/skills/prd-generator/references/`.

FD ids are continuous with the MVP stories FD-001..FD-008, which are shipped. Never reuse or
renumber an id.

## Open

| ID | Title | Context |
|----|-------|---------|
| [FD-014](FD-014-countdown-warning-beep.md) | Countdown warning beep at 10 seconds remaining | Session Execution / Preferences |
| [FD-015](FD-015-skip-break-button.md) | Skip break button ("Ready") during inter-pose delay | Session Execution |


## Shipped

The MVP stories: FD-001 (folder selection), FD-002 (session setup), FD-003 (session engine),
FD-004 (player screen), FD-005 (countdown), FD-006 (skip), FD-007 (end + summary), FD-008 (foldable
layout). Their acceptance criteria are the invariant tables in
[DOMAIN-MODEL.md](../DOMAIN-MODEL.md) and the suites named in
[ARCHITECTURE.md §11](../ARCHITECTURE.md#11-testing-strategy) — they have no ticket files of their
own; the original stubs were an early experiment and were never committed.

Since then, with a committed ticket kept as the record of how it was built:

| ID | Title | Left behind |
|----|-------|-------------|
| [FD-009](FD-009-async-reference-library.md) | The library loads off the UI thread | `INV-X-13`, `LibraryLoader`, `LoadGeneration`, `LibraryLoadState`, `LibraryLoadContractTests`, and the background-work shape in [ARCHITECTURE.md §7](../ARCHITECTURE.md#7-threading). Two acceptance criteria are open pending manual verification on a real device |

[FD-011](FD-011-tick-reports-what-changed.md) (the session says what changed) and
[FD-010](FD-010-pose-decode-off-the-tick.md) (a pose boundary never blocks the repaint loop) shipped
together, FD-011 first because both touch the same `Tick` call site. Their criteria are `INV-SES-13`,
`INV-PLY-7`, `INV-PLY-8` and the narrowed `INV-POOL-6` in [DOMAIN-MODEL.md](../DOMAIN-MODEL.md).
FD-010 grew one part beyond its ticket: the handoff bound became a function of the session's length,
because the failure budget it introduces is derived from the pool size.

[FD-012](FD-012-remembered-folder.md) (the app remembers the folder you picked) shipped after the
MVP set: the library the artist last opened is restored on launch, survives a process kill, and is
where the picker reopens. Its criteria are `INV-REF-*`, `INV-SET-P4/P5`, `INV-GRP-5`, `INV-STO-5`
and `INV-X-11` in [DOMAIN-MODEL.md](../DOMAIN-MODEL.md).

[FD-013](FD-013-folder-memory-regression.md) (remembered folder lost on close — regression of
FD-012) resolved: the save was moved to fire only after a successful folder load, and prevention
tests (`CrossActivityContractTests`, synchronicity guards in `FolderMemoryContractTests`) now pin
the isolation and ordering properties that prevent the class of regression.
