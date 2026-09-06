# FD-025 — Comment diet: `MainActivity`

Status: ready-for-agent

**Story:** _As the next person to read the folder screen, I see the wiring between Core and views, not 287 lines of prose spread through 1,047._
**Depends on:** FD-016 (the keep-list), FD-017, FD-018, FD-022, FD-024 (the Core it wires)

## Summary

`MainActivity.cs` is 287 comment lines in 1,047. It spans three bounded contexts — Reference
Library, Session Setup and Preferences — which is a known cost in
[ARCHITECTURE.md §20](../ARCHITECTURE.md), and much of its prose is that cost being re-explained in
place. Apply the [FD-016 §2](FD-016-comment-diet.md) keep-list. Lands after the Core files it wires,
so every rule it repeats has already been confirmed present in the docs.

## Model placement

| | |
|---|---|
| Context | Reference Library / Session Setup / Preferences (Android layer only) |
| Owning object | none — an Activity wires Core to views (ARCHITECTURE.md §4) |
| New Core type | no |
| New invariants | none. A rule found only here that belongs to a Core object is a finding: record it in DOMAIN-MODEL.md, and if the *rule itself* lives in the Activity rather than in Core, open a follow-up ticket instead of fixing it in this PR |
| Invariants changed | none |
| Crosses a boundary | no — no intent extra, no `Settings` property, no view id changes |

## Approach

- **Android:** `MainActivity.cs` only. Delete under FD-016 §2.1, keep under §2.2, rename under
  §2.3.
  - SAF notes — taking the `ContentResolver` from `ApplicationContext`, persistable grant flags,
    grant expiry — are §2.2.2 platform traps and stay, two lines each.
  - Lifecycle and `savedInstanceState` ordering notes stay only where a plausible edit re-breaks
    them; FD-013's save-after-successful-load ordering is one of those.
  - Everything restating what a Core object guarantees goes: that rule is in DOMAIN-MODEL.md and
    the Activity is not where it lives.
- **Core:** none.
- **Resources:** none — no view id, string or style is touched.
- **Persistence:** none.
- **Injected dependency:** none.

## Acceptance criteria

- [ ] `MainActivity.cs` is at or under 5% comment lines (≤ 38 of ~760 remaining lines), no comment
      block over two lines
- [ ] The comment-stripped file is byte-identical before and after, except FD-016 §2.3 renames
- [ ] SAF, `ContentResolver` and grant-flag notes are kept (FD-016 §2.2.2)
- [ ] The FD-013 ordering — the folder is saved only after a successful load — is still stated
      somewhere: a kept two-line comment or DOMAIN-MODEL.md
- [ ] No method named by `FolderMemoryContractTests` or `CrossActivityContractTests` is renamed
- [ ] `./nx.bat run FigureDrawing.Tests:test` green, same test count
- [ ] `FolderMemoryContractTests`, `CrossActivityContractTests` and `UiResourceContractTests` are
      unedited

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `n/a` — nothing in this file is unit testable; that is the point of the split |
| Contract | `FolderMemoryContractTests`, `CrossActivityContractTests`, `UiResourceContractTests`, `AndroidBuildTests` — run, not edited |
| E2E-model | `n/a` |
| UI (Appium) | `FolderPickerUiTests` — optional. Nothing observable changes, so run it only if the PR touched anything a §2.3 rename could reach |

## Out of scope

- `SessionActivity` → FD-026
- Splitting `MainActivity` across its three contexts. That is real work with real risk and is not
  this ticket; note it as a follow-up if the reading makes the case obvious.
- Moving any rule from the Activity into Core, even one the comments admit is misplaced

## Risks

- Contract tests name methods in this file. A §2.3 rename that catches one turns a comment PR into
  a red build — check `FolderMemoryContractTests` and `CrossActivityContractTests` for the name
  before renaming anything.
- SAF behaviour is the app's most fragile surface and its traps are genuinely uninferable. When in
  doubt on a SAF comment, keep it.

## Open questions

- none
