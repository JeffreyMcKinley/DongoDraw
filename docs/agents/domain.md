# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the
codebase. This is a **single-context** repo — one glossary, one object model, no `CONTEXT-MAP.md`.

## Before exploring, read these

- **[`docs/ARCHITECTURE.md`](../ARCHITECTURE.md)** — the standing brief. §15 is the ubiquitous
  language (the glossary); §16 lists the bounded contexts. It also fixes the Core/Android split, the
  rules for crossing that boundary, threading and lifecycle requirements, the four-tier testing
  strategy, and the anti-patterns that count as violations.
- **[`docs/DOMAIN-MODEL.md`](../DOMAIN-MODEL.md)** — one card per object: what identifies it, how
  long it lives, what it guarantees, what it may do, what it must never do. Rules are numbered
  invariants (`INV-<family>-<n>`) and the ids are stable across refactors.

This repo has no root `CONTEXT.md` and no `CONTEXT-MAP.md`; the two files above fill that role.
Where a skill says "read `CONTEXT.md`", read them instead.

There is no `docs/adr/` yet. If ADRs are ever written, they belong there, one decision per file, and
this section stops being hypothetical. Until then, **proceed silently** — don't flag the absence,
don't propose creating the directory upfront. `/domain-modeling` creates such files lazily, when a
term or a decision actually gets resolved.

## File structure

```
/
├── CLAUDE.md                  ← entry point; points here
├── docs/
│   ├── ARCHITECTURE.md        ← §15 ubiquitous language, §16 bounded contexts
│   ├── DOMAIN-MODEL.md        ← object cards + INV-<family>-<n> invariants
│   ├── adr/                   ← not created yet
│   └── prds/                  ← the issue tracker (see issue-tracker.md)
├── FigureDrawing.Core/        ← all logic writable without Android
└── FigureDrawing.Tests/
```

## Use the glossary's vocabulary

When your output names a domain concept — an issue title, a refactor proposal, a hypothesis, a test
name, a type name — use the term as ARCHITECTURE.md §15 defines it. Don't drift to synonyms the
glossary avoids.

If the concept you need isn't in the glossary, that's a signal: either you're inventing language the
project doesn't use (reconsider), or there's a real gap (note it for `/domain-modeling`).

## Cite invariants rather than restating them

An acceptance criterion, a test name, or a review comment that means an existing rule should cite
its id (`INV-SET-P5`) instead of paraphrasing it. Paraphrases drift; ids resolve.

## Flag conflicts

If your output contradicts a documented invariant or a future ADR, surface it explicitly rather than
silently overriding:

> _Contradicts INV-SES-3 — but worth reopening because…_
