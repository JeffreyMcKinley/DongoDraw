---
name: design-push
description: Push docs/design/ back up to the Claude Design theme and design projects, then read the result back from the server to verify. Use when the user asks to "push the design", "sync the design up", "send this to Claude Design", or after correcting something in docs/design/ that the Design project should also carry. Writes to shared, live projects — always confirm before pushing.
---

# Design push

Send `docs/design/` up to the two Claude Design projects.

This is the outward-facing half of the loop; `/design-pull` is the inward half. Read
[docs/design/README.md](../../../docs/design/README.md) for the model.

**A push changes live state other people see, and Claude Design keeps no version history you can
roll back to.** The repo's copy is the only undo. Treat every step below as required.

## 0. Authorize

`DesignSync` fails with a `design-system authorization` error until the user runs `/design-login`
once per session. If that appears, stop and ask them to run it.

**Run every `DesignSync` call from the main thread.** Subagents have no design authorization and
will quietly answer from the local mirror instead of the server — which looks exactly like
verification while proving nothing.

## 1. Resolve the two projects

Never assume the ids in another contributor's checkout are yours — pushing to someone else's project
is the one mistake here with no clean undo. Resolve in this order:

1. If `docs/design/sync.local.json` exists, use its `themeProjectId` and `designProjectId`.
2. Otherwise read `source.theme.projectId` and `source.design.projectId` from
   `docs/design/tokens.json`.
3. Confirm both with `get_project`, and check **`canEdit` is true** and the `name` matches what
   `tokens.json` records.

If `canEdit` is false, or the project is not one the user owns, **stop**. Tell them to create
`docs/design/sync.local.json` (gitignored) from `docs/design/sync.local.example.json`. Their own ids
come from `list_projects` for the theme, and from the design's URL
(`https://claude.ai/design/p/<id>?file=...`) for the design, which `list_projects` does not show.

`PROJECT_TYPE_PROJECT` is **not** read-only. The type governs whether a project can be attached as a
theme, not whether files can be written to it. Both projects accept writes.

## 2. Confirm before writing

Show `git diff docs/design/` — or, for uncommitted work, the specific lines changing — and get an
explicit yes naming what is going up. Never push on an inferred go-ahead.

Two things to say out loud when they apply:

- **Pushing the theme affects every design built on it**, not just this app. If a collaborator pulls
  mid-push they get the half-changed state. Ask whether the other contributors should be told first.
- If the working tree and the git index disagree, say which one is about to be pushed. A stale
  `git add` is easy to miss and the index is not what gets uploaded — the file on disk is.

## 3. Push

Only ever push the mirrored set:

| Local | Project | Remote path |
|---|---|---|
| `docs/design/nocturne/styles.css` | theme | `styles.css` |
| `docs/design/nocturne/theme.json` | theme | `theme.json` |
| `docs/design/app/<main>.dc.html` | design | same basename |
| `docs/design/app/github.md` | design | `github.md` |

**Never push `_ds/…`.** That is the design's pinned snapshot of the theme; overwriting it repoints
what the design renders with. Never push the design tool's runtime (`support.js`, `image-slot.js`,
`_ds_bundle.js`) or its state files — they are not mirrored and this folder has no valid copy.

One `finalize_plan` per project, then `write_files`:

- `finalize_plan` **requires** `deletes` even when nothing is being deleted. Pass `[]`. Omitting it
  fails with `finalize_plan requires: deletes.`
- `localDir` is the local folder (`docs/design/nocturne` or `docs/design/app`), and each file's
  `localPath` is relative to it.
- Use `localPath`, **never** inline `data`. The tool reads and uploads straight from disk, so the
  content never passes through the model context and cannot be corrupted in transit. This is what
  lets a 28KB `.dc.html` round-trip byte-clean, `&quot;`-escaped attributes and all.
- Do not push files whose content has not changed; a no-op write still bumps the project.

## 4. Verify from the server

`written: 1` is a transport result, not proof. `get_file` each pushed path back and confirm:

- the change you intended is present, quoted exactly;
- nothing else moved — spot-check a few load-bearing literals elsewhere in the file, and that the
  file still ends the way it should;
- for the `.dc.html`, that the `data-props` attribute still carries its `&quot;` escaping.

If a read-back is large, do not paste it into the reply — report the specific findings only.

## 5. Report

State plainly what is now live, what the local mirror says, and whether the git index matches both.
If the push changed a file that is also staged, remind the user to re-stage before committing.

If anything came back different from what was sent, say so immediately and stop — do not push again
to "fix" it until the difference is understood.
