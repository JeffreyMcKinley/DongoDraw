# FD-009 — Reference library loads without freezing the screen

**Story:** _As an artist, I want the app to stay responsive while it reads my folder, and to notice
images I have added since I picked it._
**Depends on:** FD-001

## Summary

Picking or restoring a reference library walks the whole folder tree and decodes up to 24 preview
thumbnails on the UI thread, inside `OnCreate`. A library of a few thousand reference images stalls
launch for seconds and can ANR on the first touch. After this, the walk and the decodes happen off
the UI thread behind a loading state, and returning to the setup screen re-derives the pool so a
folder edited in another app is picked up without re-picking it.

## Model placement

| | |
|---|---|
| Context | Reference Library |
| Owning object | `ReferenceLibrary` (unchanged rules), `MainActivity` (the threading) |
| New Core type | no — the walk is already pure and already re-runnable via `Enumerate()`. What is missing is an Android-side caller, not a domain concept. Adding a "library loader" type would be a second name for `Enumerate` (DOMAIN-MODEL.md §9). |
| New invariants | `INV-X-13` — **A library load is abandonable.** Only the most recent load may write the pool; a load whose folder has been superseded is discarded, including anything it decoded. Filed in the cross-cutting family, not `INV-GRP-*`: the rule is about the Android-side loader, and DOMAIN-MODEL.md §8 maps `INV-GRP-*` to `ReferenceLibrary` / `ReferenceLibraryTests`, which cannot reach it. |
| Invariants changed | none. `INV-GRP-1` (membership is derived, never stored) is what this ticket finally exercises in production: today `Enumerate()` has no caller outside the constructor. |
| Crosses a boundary | no new contract. The pool still crosses to the player under the bound already applied by `ReferenceLibrary.Sample`, sized by `SessionSetup.HandoffBound(config.ImageCount, MainActivity.MaxPoolHandoff)` — the session's length, with the transport's limit as the ceiling (`INV-POOL-6`). |

## Approach

- **Core:** none required. `ReferenceLibrary.Enumerate()` already re-derives the pool and is already
  unit tested; this ticket is about who calls it and on which thread. If the walk needs to stop
  early when abandoned, express that as an injected cancellation check on `IDocumentTree`, never as
  an Android type inside Core (ARCHITECTURE.md §4).
- **Android:** `MainActivity` renders the empty/loading state immediately, then performs
  `new ReferenceLibrary(...)` and the thumbnail decode loop off the UI thread, marshalling only the
  field assignment, view creation, `RenderLibrary()` and `UpdateStartState()` back through the main
  looper. A generation counter (or `CancellationTokenSource`) makes a superseded load a no-op —
  `StartSession` reads `library` directly, so a slow restore must never overwrite a folder the
  artist has since picked. Cancellation belongs in `OnDestroy`, not `OnPause`: a briefly
  backgrounded app must not come back with an empty library.
- **Android (the grid's lifetime):** the thumbnails are released in `OnStop` and repopulated in
  `OnStart`, not held until `OnDestroy` as they are today. `MainActivity` is *stopped*, not
  destroyed, while `SessionActivity` runs, so up to 24 decoded previews currently sit under every
  session and the app's real peak is grid + pose + the pose being decoded. The repopulate is only
  affordable once the decode is off the UI thread, which is this ticket — so it lands here or not
  at all. It reuses the same load path and the same generation guard: a resume that starts a
  repopulate and is then superseded must discard what it decoded.
- **Resources:** one new string for the loading state (`library_loading_text`). No new view id — the
  existing `empty_label` carries it.
- **Persistence:** none. `Settings.LastCollection` already holds the only thing that survives.
- **Injected dependency:** none new in Core.

## Acceptance criteria

- [ ] Launching with a restored folder of ~2,000 reference images paints the setup pane immediately;
      the library pane shows a loading state and then the pool count.
- [ ] Picking a second folder while the first is still loading ends with the second folder's pool —
      never the first's, and never a mixture (`INV-X-13`).
- [ ] Bitmaps decoded by an abandoned load are recycled, not attached to the grid.
- [ ] Starting a session leaves no decoded thumbnail resident: the grid is released in `OnStop`, so
      the player screen's decodes are not stacked on top of the library's.
- [ ] Returning from a session repopulates the grid without blocking the UI thread, and without
      showing an empty grid captioned as though the folder were empty.
- [ ] Returning to `MainActivity` from a finished session re-derives the pool, so images added to
      the folder in another app appear without re-picking it (`INV-GRP-1`).
- [ ] A failed or revoked folder still lands in the empty state with the folder-error message, and
      Start stays shut — the restore path must not become a crash on every launch.
- [ ] Start remains gated on `!library.IsEmpty`: while a load is in flight the pool is empty, so
      Start is disabled rather than armed over a half-built pool.
- [ ] No effect on `CompletedCount`, `SkippedCount` or `TotalDrawingTime` — this ticket never touches
      a running session.

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `ReferenceLibraryTests` — re-`Enumerate()` after the fake tree changes yields the new pool; a tree that reports nothing yields an empty pool. `INV-X-13` is Android-side; pin it with a source-shape contract test (the generation guard and the `OnDestroy` cancellation are both greppable) rather than leaving it unpinned. |
| Contract | `UiResourceContractTests` — `library_loading_text` exists. A source-shape assertion that `MainActivity.OnStop` calls `ClearThumbnails()` keeps the grid's lifetime from silently reverting to `OnDestroy`. |
| E2E-model | `n/a` — no session behaviour changes. |
| UI (Appium) | Pick a folder, then confirm the setup pane is interactive before the count appears. Justified: "the UI thread was never blocked" is not observable from Core. |

## Out of scope

- Paging or lazy-loading the thumbnail grid. The cap stays 24 decode attempts.
- Caching decoded thumbnails across launches — `INV-IMG-4` forbids it.
- Capping the pool itself. Membership stays whatever the tree reports (`INV-GRP-1`, `INV-GRP-2`);
  bounding what crosses to the player is already handled by `ReferenceLibrary.Sample`
  (`INV-POOL-6`).
- Persisting session state across process death (ARCHITECTURE.md §5).

## Risks

- The `OnStop` release above is what makes the resume path load-bearing: get the repopulate wrong
  and every return from a session shows an empty grid, which is worse than the resident memory it
  fixes. It shares the load path with the initial load for exactly this reason.
- A re-walk on every resume includes the frequent "back from a session" path. It must be off the UI
  thread before it is added, or this ticket trades a launch stall for a resume stall — and a cold
  start must not walk twice.
- `MainActivity` already spans three contexts; a threading rewrite there makes that worse. Keep the
  load in one method with one cancellation owner.
- SAF grants can expire between the permission check and the walk (`INV-GRP-5`); the background path
  must treat that as the ordinary empty answer, not an escape.

## Open questions

- Should a resume re-walk be skipped when the artist returns within a few seconds, or is every
  resume a re-walk? — decided by implementation, once the walk is off the UI thread.
