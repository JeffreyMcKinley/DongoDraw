# FD-012 — The app remembers the folder you picked

Status: shipped
Context: Reference Library
Depends on: FD-001 (folder selection)

## Why

The artist picks a folder of reference images once. Every launch after that, the app should come up
on the same library — and when they do go back to the picker, it should open where they left off.

The reported failure was the opposite of all of it: *"App doesn't save folder on close."* On a
physical device, swiping the app off the recents list lost the pick. The cause was not the folder
logic — it was the write. `Settings.Save` upserted into LiteDB's write-ahead log and returned; a
process killed before the log was folded into the datafile could leave it truncated mid-page, and a
truncated log **does not fail to open**. It reads back as the values from before the save. The
preference reverted with nothing thrown and nothing logged, which is indistinguishable from the app
having forgotten (`INV-STO-5`).

A second, subtler version of the same complaint: a folder whose read grant the platform has dropped
(permissions cleared, volume unmounted, grant trimmed) used to show the *first-run* empty state, so
"I lost your permission" and "you never picked anything" read identically on screen.

## What it does

- The picked folder is persisted as a reference (`Settings.LastCollection`) and restored on launch
  when its read grant is still held.
- A save is durable when it returns: `Save()` checkpoints, so a killed process cannot revert it
  (`INV-STO-5`). It is also a no-op when nothing changed, so writing on every pause costs nothing.
- Settings are written where each value changes, and again on `OnPause` — which first captures the
  typed setup inputs, the only values that live nowhere else. A recents swipe never reaches
  `OnDestroy`, so there is no teardown to rely on.
- A remembered folder that cannot be reopened says so (`folder_unavailable_text`) and keeps the
  reference. The four empty states — never picked, unreachable, empty, would-not-open — are four
  different messages (`INV-REF-5`).
- The picker opens *inside* the remembered folder (`EXTRA_INITIAL_URI`). Deliberately not gated on
  the grant: a revoked permission should not also cost the artist their place.
- Picking a different folder releases the grants it supersedes, and a successful restore re-takes
  the one in use — a package's persisted grants are capped and the platform drops the oldest
  (`INV-REF-4`).

## Acceptance criteria

The invariants are the criteria: `INV-REF-1`..`INV-REF-5` (§2.4), `INV-SET-P4`, `INV-SET-P5`,
`INV-GRP-5`, `INV-STO-5`, `INV-X-11` in [DOMAIN-MODEL.md](../DOMAIN-MODEL.md).

| Tier | Suite |
|---|---|
| Unit | `LibraryReferenceTests` (form, canonical spelling, grants, classification), `SettingsTests` (kill, truncated log, datafile-not-log, clean-save no-op) |
| Contract | `FolderMemoryContractTests`, `UiResourceContractTests` (the four empty-state strings, all distinct) |
| UI | `FolderPickerUiTests` — restored on relaunch, survives Back, survives `am kill`, picker reopens in the folder |

## Not in this ticket

- Moving the folder walk and the thumbnail decode off the launch path — [FD-009](FD-009-async-reference-library.md).
- Splitting `MainActivity` by context (§20 deviation 1, deferred with a stated trigger).
