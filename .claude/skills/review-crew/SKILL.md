---
name: review-crew
description: Multi-agent code review of the current diff, a branch, a PR, or named files. Fans out five specialist reviewers — quality, security, performance, test coverage, documentation — in parallel, then merges their findings into one deduplicated, severity-ranked report. Use when the user asks to "review my changes", "review this PR", "audit this file", or wants a thorough pre-commit check. For a single narrow question (just security, just perf), invoke the one matching agent directly instead.
---

# Review Crew

Fan out specialist reviewers over a target, merge their findings into one report.

The reviewers live in `.claude/agents/` and are read-only. This skill dispatches them and synthesizes; it does not fix anything unless the user asks.

## 1. Resolve the target

| User said | Target |
|---|---|
| nothing specific, "review my changes" | `git diff` + `git diff --cached` (uncommitted) |
| "review my branch" / "before I PR this" | `git diff $(git merge-base HEAD master)...HEAD` |
| a PR number | `gh pr diff <N>` — plus `gh pr view <N>` for intent |
| file or directory paths | those paths, whole-file |

Gather the diff **once**, yourself, before dispatching. Run:

```
git status --short
git diff --stat
```

If the target is empty, stop and say so. Do not dispatch reviewers over nothing.

## 2. Pick reviewers

Default: all five. Drop ones with no surface area in the diff — running a reviewer that has nothing to look at wastes tokens and produces filler findings.

| Agent | Dispatch when the diff touches |
|---|---|
| `code-quality-reviewer` | any code — effectively always |
| `security-code-reviewer` | input handling, file paths, permissions, manifest, intents, storage, credentials, third-party deps |
| `performance-reviewer` | loops, image decoding, I/O, database queries, timers, draw/measure paths, async |
| `test-coverage-reviewer` | any behavior change in `DongoDraw.Core` or an Activity, or any change to `DongoDraw.Tests` / `DongoDraw.UITests` |
| `documentation-accuracy-reviewer` | public APIs, README, `docs/`, `CLAUDE.md`, `AGENTS.md`, build or deploy commands |

State which reviewers you are running and why you skipped any.

## 3. Dispatch in parallel

Send every reviewer in **one message with multiple Agent tool calls** so they run concurrently. Sequential dispatch is the most common mistake here.

Give each agent the same brief:

- The target definition (the exact git command or the file list — not the diff text; agents re-derive it)
- The user's stated concerns, verbatim, if any
- Repo context: .NET Android app, C#, Nx workspace, `DongoDraw.Core` holds testable logic, `DongoDraw.Tests` / `DongoDraw.UITests` hold tests
- Output contract: findings as `path:line — severity — problem — fix`, no praise sections, no summary paragraph

## 4. Merge

The agents' reports are not shown to the user. You own the final report.

1. **Deduplicate.** Two reviewers flagging the same line for the same reason is one finding, tagged with both lenses.
2. **Verify before reporting.** Open the cited `path:line` for anything Critical or High. Agents hallucinate line numbers and occasionally invent code. A finding you could not confirm gets dropped or labeled `unverified`.
3. **Drop scope creep.** Findings about code the target did not touch go in a short "pre-existing, out of scope" list — or nowhere.
4. **Rank** by severity, then by blast radius.

## 5. Report

```
## Review: <target> (<N> files, <M> reviewers)

### Critical
- `path:line` — <problem>. <fix>. [security]

### Important
- `path:line` — <problem>. <fix>. [quality, performance]

### Minor
- `path:line` — <problem>. <fix>. [docs]

### Out of scope
- <pre-existing issues noticed, one line each>
```

Clean review: say `No findings` under the heading and name what was checked. Do not manufacture minor findings to fill space.

## 6. Fixing

Do not fix by default — the reviewers are read-only for a reason and the user may disagree with a finding.

If the user asks for fixes, apply them yourself in the main thread, most severe first, and re-run the affected tests via `./nx.bat run <project>:test`.

## Notes

- Distinct from the built-in `/code-review` skill, which is a single-pass correctness-and-cleanup review. Use `review-crew` when breadth across security/perf/tests/docs matters; use `/code-review` for a fast focused pass.
- Reviewer prompts are adapted from `anthropics/claude-code-action` `.claude/agents/`.
