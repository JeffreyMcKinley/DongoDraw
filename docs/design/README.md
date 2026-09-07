# Design source

The app's UI comes from **two** Claude Design projects, and it matters which is which:

| | Project | Type | What it is |
|---|---|---|---|
| **Theme** | Nocturne · `b269d325-…` | `PROJECT_TYPE_DESIGN_SYSTEM` | The design *system* — colour ramps, spacing scale, radii, component classes. Writable by `DesignSync`. |
| **Design** | Figure Drawing Practice App · `ae2fad04-…` | `PROJECT_TYPE_PROJECT` | The *design* — the five screens, their states, and the literal sizes and washes they are drawn with. Readable by `DesignSync`, not writable as a design system. |

[The design][design] is the thing a designer opens and edits. Nocturne is the theme it is built on.
Several of the app's numbers — the pose stage, the overlays, the 16% chip wash, the rail — exist
only in the design; Nocturne has no opinion about them.

[design]: https://claude.ai/design/p/ae2fad04-3c3e-4595-8622-dc6366331e21?file=Figure+Drawing+App.dc.html

Neither project can be shared between two individual Claude accounts — there is no shared workspace
below a Team plan. So **the repo, not either Design project, is the source of truth**, and both
people sync through git.

```
    designer's projects ──DesignSync──┐
                                      ├──►  docs/design/  ──►  Resources/values/*.xml
   developer's projects ──DesignSync──┘         (git)               (the app)
```

## What is in here

| File | What it is |
|---|---|
| `app/Figure Drawing App.dc.html` | Verbatim copy of the design. Markup plus a `<script type="text/x-dc">` component class holding the state and the computed styles. |
| `app/github.md` | The design project's own record of this repo — a screen-to-files map. **This copy is ahead of the remote**; see below. |
| `nocturne/styles.css` | Verbatim copy of the theme's token file. |
| `nocturne/theme.json` | Verbatim copy of the theme's metadata (palette seed, fonts, density, radius). |
| `tokens.json` | The reviewed bridge, and the only hand-maintained file here. |

The three verbatim files are **pulled artefacts**. Hand-editing one is how you lie to the tests.
The exception is `app/github.md`, which the repo is better placed to keep accurate than the design
project is — corrections to it flow back on the next push.

`support.js`, `image-slot.js` and `_ds_bundle.js` are the design tool's own runtime and are
deliberately not mirrored — there is nothing in them to port, and the design will not render from
this folder. Open [the design][design] to look at it.

## Review checklist

Design sync is a workflow around this repo, not part of the app, so none of it is tested — nothing
in the build reads `tokens.json`, and the app ships identically whether this folder is correct or
empty. What replaces the tests is reading the diff. After a pull, walk these:

- **Tokens.** For each changed value in `nocturne/styles.css` or the design file, does `tokens.json`
  still claim the old one? Either transcribe it into `Resources/values/` or record the divergence in
  that token's `note`.
- **Washes.** Nocturne writes `color-mix(in srgb, <token> N%, transparent)`; Android has no such
  function, so the percentage is baked into the alpha byte — `#29…` is 16%, `#1A…` is 10%,
  `#E6…` is 90%. If a percentage moved, recompute (`round(N/100 × 255)`).
- **Spacing.** The scale is fractional px upstream and whole dp here, so rounding differences are
  expected. `space_4` (11.2px → 12dp) and `space_6` (16.8px → 16dp) are deliberate nudges to the 4dp
  grid, not transcription errors.
- **New tokens.** Anything added upstream is either bridged or written off in `unusedCss` with a
  reason. An unlisted token is one nobody has looked at.
- **The theme pin.** The design embeds its own snapshot of Nocturne under
  `_ds/nocturne-b269d325-…/`. If the design stops linking that path it has been repointed at a
  different system and this folder is tracking the wrong theme.
- **The screen map.** Every repo path in `app/github.md` should still exist. The 2026-08-16 map
  named two files that had been renamed, and nothing on the design side would ever have noticed.
- **Copy.** The setup screen's labels exist twice — in the design, and in
  `Resources/values/strings.xml` (`seconds_label_text`, `count_label_text`). They drift silently.

None of this tells you whether the app *looks* like the design; nothing can. It tells you the design
has not moved underneath a number the app copied out of it.

## The loop

There is no shell command for this — `DesignSync` rides a claude.ai login, with no CLI and no
endpoint to `curl`, so nothing in `scripts/` can drive it. The commands are slash commands:

```
/design-login          once per session, before either of the below
/design-pull           both projects → docs/design/, then diff + review
/design-push           docs/design/ → both projects, then read back to verify
```

**First run on a new machine:** copy `sync.local.example.json` to `sync.local.json` (gitignored) and
fill in your own two project ids. Without it the skills fall back to the ids in `tokens.json`, which
belong to whoever set the mirror up — harmless on a pull, but a push would write into someone else's
project, and Claude Design keeps no version history to undo that.

The steps below are what those skills do, for when you want to drive it by hand.

### Pulling a design change in

1. `DesignSync` `get_file` against both projects; overwrite the four verbatim files.
2. `git diff docs/design/` and read it against the review checklist above. The diff is the signal —
   there is no test to go red.
3. Per moved value: transcribe it into `Resources/values/` and update `tokens.json`, or record why
   the app diverges in that token's `note` and set `divergesFromDc`.
4. Commit `docs/design/` and `Resources/values/` together, so the design decision and its Android
   consequence sit in one diff.

### Pushing back

**Both projects are writable**: `list_files` → `finalize_plan` → `write_files`. Verified end to end
on 2026-09-06 by pushing a one-word copy change into the design and reading it back.

`PROJECT_TYPE_PROJECT` does not mean read-only. The type gate governs whether a project *behaves* as
a design system — whether it can be attached as a theme — not whether files can be written to it.
`list_projects` is filtered to design systems, which is why the design never appears there; reach it
by id.

Push CSS variables to Nocturne, not Android XML — the theme's vocabulary is CSS.

Two mechanical notes, both learned the hard way:

- `finalize_plan` **requires** a `deletes` array even when nothing is being deleted. Pass `[]`.
- Prefer `localPath` over inline `data`. The tool reads and uploads straight from disk, so a large
  file never passes through the model's context and cannot be corrupted in transit. A full
  `.dc.html` round-tripped byte-clean this way, `&quot;`-escaped props attribute and all.

### Collaborating

Neither person can see the other's projects, so **a Claude Design project is never the shared
state**. The pull/push above is each person's private on-ramp; `docs/design/` is the handoff. A
design change not committed here has not happened. Review a large `styles.css` or `.dc.html` diff as
a design change, not a mechanical sync — that diff *is* the design review.

## Open items

- **`app/github.md` is ahead of the remote.** Its 2026-08-16 screen map named
  `FolderImageEnumerator.cs` and `Data/AppSettings.cs`, neither of which exists — the real types are
  `ReferenceLibrary` and `Settings`. Corrected here; push it back to the design project.
- **`rail_width` genuinely disagrees.** The design draws the rail at 288px, the app at 328dp. The
  app is probably right (four tool chips keep their labels in one row); the design should follow.
- **`pendingFromDesign` has four entries** — decisions the design has made that the app has not
  built. Two have tickets (#9, #10, both `ready-for-agent`); Blur and zoom have none.
- **Copy is not guarded.** The setup screen's labels exist twice — `Seconds per image`,
  `Number of images` and `Break between poses` are in the design, and `seconds_label_text` /
  `count_label_text` in `Resources/values/strings.xml`. Nothing asserts they agree, so a reworded
  label on either side goes unnoticed. `tokens.json` has no place for copy today; the natural fix is
  a `strings` array pairing each design literal with its `strings.xml` name, checked the same way
  the `dc` literals are.

## Gotchas

- **Do not round-trip these files through PowerShell 5.1.** `Get-Content -Raw` + `Set-Content`
  mangles the em dashes in Nocturne's comments into `â€"`. Edit with a tool that preserves UTF-8.
- `DesignSync` needs `/design-login` once per session before any method works.
- `--font-heading` is not bridged as a token. Inter is bundled at `Resources/font/` and reaches
  views through the theme's `textAppearance`; `TypefaceContractTests` guards that separately, and
  `ARCHITECTURE.md` §3 explains why `android:fontFamily` on a theme silently does nothing.
