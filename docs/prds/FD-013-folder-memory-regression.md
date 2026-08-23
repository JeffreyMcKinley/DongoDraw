# FD-013 — Remembered folder lost on close (regression)

Status: needs-triage
Context: Reference Library
Depends on: FD-012

## Why

FD-012 shipped: the artist picks a folder once and the app restores it on every launch. After
FD-010 (pose decode off the tick) and FD-011 (tick reports what changed) landed, the folder is no
longer restored — closing and reopening the app shows the first-run empty state instead of the
previously selected library.

The behaviour FD-012 proved (unit, contract, and UI tiers) is not new; the invariants it cites
(`INV-SET-P4`, `INV-SET-P5`, `INV-STO-5`, `INV-GRP-5`, `INV-REF-1`..`INV-REF-5`) have not been
changed. This is a regression in the wiring between those rules, not a missing rule.

## Model placement

| | |
|---|---|
| Context | Reference Library / Preferences |
| Owning object | `Settings` (persistence), `LibraryReference` (restore logic) |
| New Core type | no |
| New invariants | none — every rule already exists; this ticket restores compliance |
| Invariants changed | none |
| Crosses a boundary | `Settings.LastCollection` ↔ `MainActivity.RestoreLastFolder()` |

## Likely regression area

FD-010 moved session construction off the UI thread and added prefetching; FD-011 changed
`Tick()`'s return type from `bool` to `SessionTick`. Both touched `SessionActivity` and the
startup path. The folder persistence chain runs through `MainActivity`:

- `OnCreate` → `RestoreLastFolder()` (line ~120)
- `OnActivityResult` → `TakePersistableUriPermission` → `Settings.LastCollection =` (line ~526)
- `OnPause` → `Settings.Save()` (the backstop write)
- `RememberedTree()` reads `LastCollection` back on launch

A refactor that altered when or whether `Save()` is called, changed the `OnPause` backstop, or
broke the `RestoreLastFolder` call order after the session-construction move could produce this
symptom: the value is written but not read back, or written to the WAL but never checkpointed
before the process dies.

## Acceptance criteria

These are FD-012's criteria, restated as a regression checklist:

- [ ] Picking a folder, closing the app (swipe off recents), and reopening it shows the same
      library — not the first-run empty state (`INV-SET-P5`, `INV-STO-5`)
- [ ] The picker reopens inside the remembered folder (`EXTRA_INITIAL_URI`) even after a cold
      start
- [ ] A folder whose read grant has been revoked shows `folder_unavailable_text`, not the
      first-run state (`INV-REF-5`, `INV-GRP-5`)
- [ ] `Settings.Save()` checkpoints before returning — a process killed immediately after a
      pick does not revert the preference (`INV-STO-5`)
- [ ] Existing `FolderMemoryContractTests` pass without modification
- [ ] Existing `FolderPickerUiTests` (restored on relaunch, survives kill, picker hint) pass
      without modification
- [ ] `CompletedCount`, `SkippedCount`, `TotalDrawingTime` — unchanged (this ticket touches no
      session logic)

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `SettingsTests` — kill, truncation, checkpoint. `LibraryReferenceTests` — form, grant, classification. Both suites must stay green; if they already are, the regression is in the Activity wiring, not in Core |
| Contract | `FolderMemoryContractTests` — the 15 contract cases covering the restore chain. Green here + broken on device = the Activity is not calling the chain correctly |
| E2E-model | `n/a` |
| UI (Appium) | `FolderPickerUiTests` — `PickedFolder_IsRestoredOnRelaunch`, `PickedFolder_SurvivesTheProcessBeingKilled`, picker hint. These are the definitive regression tests for this ticket |

## Approach

- **Diagnosis first.** Run the unit and contract tiers. If green, the regression is in `MainActivity`
  wiring — inspect the FD-010/FD-011 diff for changes to `OnCreate`, `OnPause`, `OnActivityResult`,
  or anything that altered the order of `RestoreLastFolder` relative to the new session-construction
  path.
- **Core:** no changes expected — the domain rules are intact.
- **Android:** fix the broken wiring in `MainActivity` or `SessionActivity`, wherever the save or
  restore call was dropped or reordered.
- **Resources:** none.
- **Persistence:** none new — `Settings.LastCollection` already exists.
- **Injected dependency:** none.

## Out of scope

- Any new folder-memory behaviour beyond what FD-012 delivered.
- Moving the folder walk off the launch path — that is [FD-009](FD-009-async-reference-library.md).
- Splitting `MainActivity` by context.

## Risks

- If the regression is in the `OnPause` checkpoint path, the fix may interact with FD-010's
  background-thread session construction — saving settings while a decode thread is running needs
  the same thread-safety `Settings` already has (single-threaded writes at named moments,
  `INV-SET-P4`).
- A fix that re-orders `OnCreate` calls must not break the session-loading state FD-010 introduced.
