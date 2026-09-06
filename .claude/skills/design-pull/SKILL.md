---
name: design-pull
description: Pull the Claude Design theme and design into docs/design/, then diff the result and walk the review checklist. Use when the user asks to "pull the design", "sync the design down", "get the latest design", "did the design change", or before starting UI work that should follow a designer's changes. Read-only against the remote — it never writes to a Design project.
---

# Design pull

Bring the two Claude Design projects into `docs/design/` and report what moved.

The repo is the source of truth for the look; each contributor's Claude Design account is a personal
on-ramp, because two individual accounts cannot share a Design project. Read
[docs/design/README.md](../../../docs/design/README.md) for the whole model — this skill is the
mechanical half.

**This skill never writes to a Design project.** Pushing is `/design-push`.

## 0. Authorize

`DesignSync` fails with a `design-system authorization` error until the user runs `/design-login`
once per session. If that error appears, stop and ask them to run it — you cannot run it for them.

**Run every `DesignSync` call from the main thread.** Subagents have no design authorization. A
subagent asked to read a design project will not error usefully; it falls back to reading the local
mirror and reports that as if it came from the server.

## 1. Resolve the two projects

Never assume the ids in another contributor's checkout are yours. Resolve in this order:

1. If `docs/design/sync.local.json` exists, use its `themeProjectId` and `designProjectId`.
2. Otherwise read `source.theme.projectId` and `source.design.projectId` from
   `docs/design/tokens.json`.
3. Confirm both with `get_project`. Check the returned `name` matches what `tokens.json` records.

If `get_project` errors, or returns a project the user does not own, this checkout's ids belong to
someone else. Stop and tell them to create `docs/design/sync.local.json` (gitignored) from
`docs/design/sync.local.example.json`, and how to find their own ids:

- **Theme** — `list_projects` returns design-system projects the account can write to.
- **Design** — *not* in `list_projects`; that listing is filtered to design systems. The id is in
  the design's URL: `https://claude.ai/design/p/<id>?file=...`.

## 2. Read the remote

`list_files` on both projects first — it is cheap and catches a renamed or added file before you
fetch anything.

Then `get_file` each mirrored path:

| Local | Project | Remote path |
|---|---|---|
| `docs/design/nocturne/styles.css` | theme | `styles.css` |
| `docs/design/nocturne/theme.json` | theme | `theme.json` |
| `docs/design/app/<main>.dc.html` | design | same basename |
| `docs/design/app/github.md` | design | `github.md` |

Take the `.dc.html` name from `list_files` rather than assuming it — it is the design's own file
name and a designer can rename it.

Do **not** mirror `support.js`, `image-slot.js`, `_ds_bundle.js`, `_adherence.oxlintrc.json`,
`_ds_manifest.json`, `.thumbnail` or `.image-slots.state.json`. They are the design tool's runtime
and state; there is nothing in them to port and the design will not render from this folder anyway.

`_ds/<theme>/styles.css` inside the design project is the theme snapshot the design actually renders
with. Do not mirror it either, but **do** fetch it and compare against the theme project's
`styles.css`. If they differ, the design is rendering with a theme this repo is not tracking — say
so prominently; that is a finding, not a detail.

## 3. Write and diff

Write each file with the `Write` tool. Do not round-trip through PowerShell 5.1 — `Get-Content -Raw`
plus `Set-Content` mangles the em dashes in the theme's comments into `â€"`.

Then `git diff docs/design/` and read it. Git may warn `LF will be replaced by CRLF`; that is the
repo's normalization, not corruption.

## 4. Report against the checklist

Nothing here is tested — no build step reads `docs/design/`, and no test goes red when a token
moves. The diff is the whole signal, so read it against the review checklist in
[docs/design/README.md](../../../docs/design/README.md) and report:

- **Tokens that moved** — name the old and new value, and whether `tokens.json` still claims the old
  one. Each needs either transcribing into `Resources/values/` or a `note` recording the divergence.
- **Washes** — Android has no `color-mix()`, so a percentage is baked into the alpha byte:
  `round(N/100 × 255)`. `#29…` is 16%, `#1A…` is 10%, `#E6…` is 90%, `#EB…` is 92%.
- **Spacing** — fractional px upstream, whole dp here, so rounding differences are expected. The
  deliberate nudges to the 4dp grid are recorded in `tokens.json`; do not "fix" them.
- **New tokens** — anything added upstream must be bridged or written off in `unusedCss` with a
  reason. An unlisted token is one nobody has looked at.
- **Screen map** — every repo path in `app/github.md` should still exist. Check them; a Core rename
  leaves the design's record of this repo pointing at nothing, and nothing on the design side would
  ever notice.
- **Copy** — the setup screen's labels exist twice, in the design and in
  `Resources/values/strings.xml` (`seconds_label_text`, `count_label_text`). They drift silently.
- **`pendingFromDesign`** — if a pinned literal is gone, the designer dropped that idea and the
  entry is stale.

End with what the user should do next, not just what changed. If nothing moved, say the mirror is
already current and stop — do not invent work.
