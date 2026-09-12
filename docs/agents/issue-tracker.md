# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues on
[`JeffreyMcKinley/DongoDraw`](https://github.com/JeffreyMcKinley/DongoDraw/issues). Use the
`gh` CLI for all operations. The repo is private; issues are visible to collaborators only.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body-file <path>`. Use `--body-file` rather
  than an inline `--body` — ticket bodies run to a hundred lines and must not go through shell
  argument quoting.
- **Read an issue**: `gh issue view <number> --comments`.
- **List issues**: `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'` with appropriate `--label` and `--state` filters.
- **Comment on an issue**: `gh issue comment <number> --body "..."`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`

Infer the repo from `git remote -v` — `gh` does this automatically when run inside a clone.

## Ticket identity

The issue number **is** the id. There is no separate ticket id, no index file to keep in sync, and
nothing to renumber. Cite work as `#14`, in commit messages, code comments and docs alike.

Bodies are written with the `prd-generator` skill, against
[ARCHITECTURE.md](../ARCHITECTURE.md) and [DOMAIN-MODEL.md](../DOMAIN-MODEL.md) — a spec that
contradicts them is wrong, not visionary. Acceptance criteria cite invariant ids
(`INV-<family>-<n>`) rather than restating rules in new words.

Links inside an issue body must be **absolute** —
`https://github.com/JeffreyMcKinley/DongoDraw/blob/master/docs/...`. GitHub does not resolve
relative paths in issue bodies.

## Labels

Two axes, both required on a new issue:

- **Triage state** — one of the five roles in [triage-labels.md](triage-labels.md). An issue with
  no state label counts as `needs-triage`.
- **Bounded context** — `context:reference-library`, `context:session-setup`,
  `context:session-execution`, `context:preferences`, `context:rendering`, or `context:repo` for
  build/test/enforcement work that belongs to no single context. More than one is allowed where the
  work genuinely spans contexts.

Shipped work is a **closed issue**, not a label.

`gh issue create --label <missing>` fails outright rather than creating the label. Check with
`gh label list` before inventing a new one.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature
requests; `/triage` reads this flag.)_

GitHub shares one number space across issues and PRs, so a bare `#3` may be either — resolve with
`gh pr view 3` and fall back to `gh issue view 3`. In this repo `#1`–`#3` are pull requests and
issues begin at `#4`.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Run `gh issue view <number> --comments`.

## History

Until September 2026 tickets were markdown files under `docs/prds/`, ids FD-001 to FD-028. The
files were migrated into issues and deleted; the mapping is `FD-0NN` → `#(NN - 5)`, so the ticket
numbered 009 is `#4` and 028 is `#23`. FD-001 to FD-008 are the MVP stories, which never had files — their
acceptance criteria are the invariant tables in [DOMAIN-MODEL.md](../DOMAIN-MODEL.md) and the
suites named in [ARCHITECTURE.md §11](../ARCHITECTURE.md#11-testing-strategy). Those eight ids
survive in code comments as provenance and resolve to no issue.

## Wayfinding operations

`/wayfinder` is not installed in this repo. If it is added, its `wayfinder:map` and
`wayfinder:<type>` labels do not exist yet and must be created with `gh label create` before the
first run — `gh issue create --label <missing>` fails rather than creating them.
