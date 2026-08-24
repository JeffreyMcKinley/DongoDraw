# Issue tracker: Local Markdown (`docs/prds/`)

Issues and specs for this repo live as markdown files in `docs/prds/`, indexed by
[`docs/prds/README.md`](../prds/README.md). The `gh` CLI is installed and `origin` points at
`github.com/JeffreyMcKinley/FigureDrawing`, so `gh pr` / `gh release` work — but there is no GitHub
Issues workflow. Never file, read or close a ticket with `gh issue`; the markdown files are the
tracker.

## Conventions

- **One file per unit of work**: `docs/prds/FD-0NN-<slug>.md`. A multi-ticket feature is a longer
  PRD in the same folder, with one FD id per shippable ticket.
- **Ids are continuous** from FD-001 and permanent. Never reuse, never renumber. The next id is one
  past the highest that appears anywhere in `docs/prds/`, shipped ids included.
- **Register the ticket**: every new file also gets a row in the `## Open` table of
  `docs/prds/README.md` (id, title, bounded context). Closing it moves the row to `## Shipped`.
- **Write it with the `prd-generator` skill** — templates live in
  `.claude/skills/prd-generator/references/`. Its output already uses the repo's ubiquitous
  language, places the work in a bounded context, splits it across Core/Android, and names its
  test tier.
- **Ground truth wins**: a PRD that contradicts [ARCHITECTURE.md](../ARCHITECTURE.md) or
  [DOMAIN-MODEL.md](../DOMAIN-MODEL.md) is wrong, not visionary. Acceptance criteria cite invariant
  ids (`INV-<family>-<n>`) rather than restating rules in new words.
- **Triage state** is a `Status:` line near the top of the file, holding one of the role strings in
  [triage-labels.md](triage-labels.md). A ticket with no `Status:` line counts as `needs-triage`.
- **Comments and conversation history** append to the bottom under a `## Comments` heading, newest
  last, each entry dated.

## When a skill says "publish to the issue tracker"

Create `docs/prds/FD-0NN-<slug>.md` and add its row to the `## Open` table in
`docs/prds/README.md`. Both, in one change — an unregistered file is invisible to everything that
reads the index.

## When a skill says "fetch the relevant ticket"

Read `docs/prds/FD-0NN-*.md`. The user normally passes the id (`FD-010`) or the path directly; an
id alone resolves by glob against `docs/prds/`.

## Wayfinding operations

Used by `/wayfinder`. Exploration is not requirements — keep unresolved fog out of `docs/prds/`,
which is the committed record of planned work.

- **Map**: `.scratch/<effort>/map.md` — the Notes / Decisions-so-far / Fog body.
- **Child ticket**: `.scratch/<effort>/issues/NN-<slug>.md`, numbered from `01`. A `Type:` line
  records the ticket type (`research`/`prototype`/`grilling`/`task`); a `Status:` line records
  `claimed`/`resolved`.
- **Blocking**: a `Blocked by: NN, NN` line near the top. Unblocked when every file it lists is
  `resolved`.
- **Frontier**: scan `.scratch/<effort>/issues/` for files that are open, unblocked, and unclaimed;
  lowest number wins.
- **Claim**: set `Status: claimed` and save before any work.
- **Resolve**: append the answer under an `## Answer` heading, set `Status: resolved`, then append a
  pointer to the map's Decisions-so-far.
- **Graduating**: when an effort resolves into work worth building, write it up as an FD ticket in
  `docs/prds/` and link back to the map. `.scratch/` is scratch and is not the deliverable.
