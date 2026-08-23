# PRDs — FigureDrawing

Requirements docs for work that is planned but not built. One file per unit of work, written
against [ARCHITECTURE.md](../ARCHITECTURE.md) and [DOMAIN-MODEL.md](../DOMAIN-MODEL.md) — a PRD that
contradicts them is wrong, not visionary. Written with the `prd-generator` skill; the templates live
in `.claude/skills/prd-generator/references/`.

FD ids are continuous with the MVP stories FD-001..FD-008, which are shipped. Never reuse or
renumber an id.

## Open

| ID | Title | Context |
|----|-------|---------|
| [FD-009](FD-009-async-reference-library.md) | Reference library loads without freezing the screen | Reference Library |
| [FD-013](FD-013-folder-memory-regression.md) | Remembered folder lost on close (regression of FD-012) | Reference Library |

FD-009 comes out of the repo-wide review of 2026-08-16, along with FD-010 and FD-011, which have
since shipped. It is the remaining main-thread decode: the folder walk and up to 24 preview
thumbnails inside `OnCreate`. FD-010 established how this app does background work and abandonment
(`SessionActivity.PrefetchUpcoming` / `DecodeAhead` / `CancelPrefetch`, ARCHITECTURE.md §7), so the
shape FD-009 needs is now written down rather than hypothetical.

## Shipped

FD-001 (folder selection), FD-002 (session setup), FD-003 (session engine), FD-004 (player screen),
FD-005 (countdown), FD-006 (skip), FD-007 (end + summary), FD-008 (foldable layout). Their acceptance
criteria are the invariant tables in [DOMAIN-MODEL.md](../DOMAIN-MODEL.md) and the suites named in
[ARCHITECTURE.md §11](../ARCHITECTURE.md#11-testing-strategy). The original ticket stubs were an
early experiment and were never committed.

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
