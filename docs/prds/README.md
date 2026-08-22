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
| [FD-010](FD-010-pose-decode-off-the-tick.md) | A pose boundary never blocks the repaint loop | Session Execution |
| [FD-011](FD-011-tick-reports-what-changed.md) | The session says what changed, not just that something did | Session Execution |

FD-009, FD-010 and FD-011 all came out of the repo-wide review of 2026-08-16. FD-009 shipped first because it was the
prerequisite in practice: it established how this app does background work and abandons it, and that
shape is now written down in [ARCHITECTURE.md §7](../ARCHITECTURE.md#7-threading). **FD-010 still
specifies the pre-FD-009 shape** (marshalling back through the ticker) and must be re-specified
against §7 before it is picked up — including where its prefetch is abandoned, which is a different
answer from the library's for the reason §7 gives. FD-011 is small and independent — a rule currently
living in an Activity moving into Core — but it touches the same `Tick` call site as FD-010, so do it
first if both are in flight.

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

**Remembering the folder** is an FD-001 follow-on rather than an id of its own: the library the
artist last opened is restored on launch and is where the picker reopens. Its criteria are
`INV-SET-P5` and `INV-X-11` in [DOMAIN-MODEL.md §5.1 / §7](../DOMAIN-MODEL.md), enforced by
`LibraryReference` and covered by `LibraryReferenceTests`, `FolderMemoryContractTests` and the
folder-memory tests in `FolderPickerUiTests`.
