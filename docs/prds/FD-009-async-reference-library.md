# FD-009 — Reference library loads without freezing the screen

Status: shipped

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
| Owning object | `ReferenceLibrary` (unchanged rules), `LibraryLoader` (the threading — see As built) |
| New Core type | No new *domain* object. `LibraryLoader` is the Android-side caller the walk was missing: it lives in the app project because it holds decoded `Bitmap`s. Two non-domain types did land in Core, both added in review: `LoadGeneration` (which load may publish) and `LibraryLoadState` (whether a re-walk keeps the pool), filed with `BitmapMath` and `GridContrast` as supporting types. They are not in the catalogue (DOMAIN-MODEL.md §1; ARCHITECTURE.md §3 and §16 place it as a supporting type): it holds no domain rule and names nothing in the ubiquitous language. They are in Core because that is the only place these rules can be executed by a test. |
| New invariants | `INV-X-13` — **A library load is abandonable.** Stated canonically in [DOMAIN-MODEL.md §7](../DOMAIN-MODEL.md); read it there rather than from this row, which is the pre-implementation sketch. In short: only the most recent load may write the pool, a load that is superseded *or abandoned* is discarded along with anything it decoded and including its failures. Filed in the cross-cutting family, not `INV-GRP-*`, because DOMAIN-MODEL.md §8 maps `INV-GRP-*` to `ReferenceLibrary` / `ReferenceLibraryTests`, which cannot reach it. |
| Invariants changed | none. `INV-GRP-1` (membership is derived, never stored) is what this ticket finally exercises in production. Note the mechanism changed in build: the pool is re-derived by constructing a *fresh* `ReferenceLibrary` per load, so `Enumerate()` still has no caller outside the constructor — see As built. |
| Crosses a boundary | no new contract. The pool still crosses to the player under the bound already applied by `ReferenceLibrary.Sample`, sized by `SessionSetup.HandoffBound(config.ImageCount, MainActivity.MaxPoolHandoff)` — the session's length, with the transport's limit as the ceiling (`INV-POOL-6`). |

## Approach

> As planned, and superseded in five places by **As built** below — the owner of the walk, where the
> abandonment check lives, which lifecycle methods abandon, how the pool is re-derived, and the UI
> test. Read them together.

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
      the library pane shows a loading state and then the pool count. **Pending manual verification**
      — the emulator harness seeds 70-byte images, so the stall this removes is not reproducible
      there. Built and behaving on a small folder; see *Not verified automatically*.
- [x] Picking a second folder while the first is still loading ends with the second folder's pool —
      never the first's, and never a mixture (`INV-X-13`).
- [x] Bitmaps decoded by an abandoned load are recycled, not attached to the grid.
- [ ] Starting a session leaves no decoded thumbnail resident: the grid is released in `OnStop`, so
      the player screen's decodes are not stacked on top of the library's. **Pending manual
      verification** — the release is pinned by a contract test and the grid is observably rebuilt on
      return, but "not resident" needs `dumpsys meminfo`; see *Not verified automatically*.
- [x] Returning from a session repopulates the grid without blocking the UI thread, and without
      showing an empty grid captioned as though the folder were empty.
- [x] Returning to `MainActivity` from a finished session re-derives the pool, so images added to
      the folder in another app appear without re-picking it (`INV-GRP-1`).
- [x] A failed or revoked folder lands in the empty state with Start shut, and the restore path never
      becomes a crash on every launch. **Split as built**, because the two cases are not the same
      outcome: a folder that *fails* to open is captioned `folder_error_text`, while a grant that has
      simply expired gets the plain empty state, since `INV-GRP-5` calls that an expected outcome and
      captioning it as a failure would make it indistinguishable from a broken provider. Covers the
      grant being revoked *while the screen is stopped*: `OnStart` finds nothing to restore and
      resets, rather than leaving a stale count over a dead pool.
- [x] Start remains gated on `!library.IsEmpty`, and is never armed over a half-built pool.
      **Amended as built:** a re-walk of the folder *already loaded* keeps the previous pool, so Start
      stays armed over the previous *complete* pool instead of greying out on every return from a
      session — the app's most-travelled path. A first pick, or a pick of a different folder, still
      clears to empty and disables Start. The pool is still only ever replaced whole, so "never a
      mixture" holds.
- [x] No effect on `CompletedCount`, `SkippedCount` or `TotalDrawingTime` — this ticket never touches
      a running session.

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `ReferenceLibraryTests` — the re-walk and empty-tree cases already existed; added `ATreeThatStopsAnswering_YieldsWhateverItGotTo` for the abandoned walk. `LoadGenerationTests` and `LibraryLoadStateTests` execute the two rules a source-shape test cannot tell from their own inverses. `SourceShapeTests` covers the contract tier's own reader. |
| Contract | `UiResourceContractTests` — `library_loading_text` exists. New `LibraryLoadContractTests` pins `INV-X-13`: the static worker, the guard orderings, the `OnStop`/`OnDestroy` abandon and the `OnStart` cold-start guard. Machinery shared out into `SourceShape`. |
| E2E-model | `n/a` — no session behaviour changes. |
| UI (Appium) | `ReturningFromASession_RebuildsTheGridAndPicksUpNewImages`, which asserts both halves from one session round trip. The originally-specified interactivity test was dropped as unfalsifiable — see As built. |

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

- ~~Should a resume re-walk be skipped when the artist returns within a few seconds, or is every
  resume a re-walk?~~ **Every resume is a re-walk, no debounce.** The grid was released in `OnStop`,
  so a rebuild is unavoidable regardless of how briefly the artist was away; a time-based skip would
  need a clock in the Activity and a second path through `RenderLibrary` to save a walk that no
  longer blocks anything.

## As built

Where the implementation departed from the plan above, and why. The code is the record; this is the
reasoning that would otherwise be lost.

- **The load lives in `LibraryLoader`, not in `MainActivity`.** The Risks section argued for keeping
  it in one method inside the Activity. ARCHITECTURE.md §20 finding #1 asked the opposite — that the
  next feature touching `MainActivity` split it by context — and that won: the walk, the decodes, the
  SAF adapter and the abandonment guard moved out, and the Activity kept view wiring and lifecycle.
  The "one cancellation owner" requirement is met by the loader *being* that owner. §20 #1 is updated.
- **Abandonment is an `int` generation, not a `CancellationTokenSource`.** Nothing on this path is a
  cancellation-aware API, so every check is an ordinary `if`; a token would have bought a disposal
  puzzle and nothing else.
- **The walk's early stop lives in the SAF adapter's constructor, not on `IDocumentTree`.** The
  Approach suggested an injected cancellation check on the port. The adapter is already Android-side,
  so a `Func<bool>` parameter there achieves the same with **zero Core change** — and a cancelled
  query is byte-for-byte the answer `INV-TREE-4` already gives a revoked grant, a shape the domain
  models and tests already. Putting a threading concept on the port would have changed every Core
  signature and both fake trees to buy nothing.
- **The load is abandoned in `OnStop` as well as `OnDestroy`.** The Approach named only `OnDestroy`.
  Without the `OnStop` abandon, a load landing after the grid was released repopulates it — putting
  back the 24 previews that release exists to remove. Still never `OnPause`, for the stated reason.
- **Two unit tests this ticket asked for already existed** (the re-walk case, since renamed to `ReWalking_PicksUpAnEditedFolder` when `Enumerate()` became private, and
  `Empty_HasNoRootAndNoImages`). What was genuinely missing was the abandoned-walk case, added as
  `ATreeThatStopsAnswering_YieldsWhateverItGotTo`: an abandoned walk yields a *partial*
  pool, which is safe only because the loader discards it before it can be shown.
- **The Appium interactivity test was dropped.** `SeedDefaultFolder` pushes 1×1 PNGs, so the seeded
  folder loads faster than Appium can complete one element query — the test would have passed
  identically against the synchronous code it was meant to catch. Replaced with two tests that wait
  on terminal states: the grid is rebuilt after a session (this ticket's top risk) and images added
  while away appear on return (`INV-GRP-1`, observable in no other tier) — later merged into one
  test, `ReturningFromASession_RebuildsTheGridAndPicksUpNewImages`. `INV-X-13`
  itself is not observable through this harness and stays pinned by `LibraryLoadContractTests`.
- **Three existing Appium assertions had to be repaired**, not as cleanup but as required work: they
  read the library the instant a pick or relaunch returned, which an asynchronous load turns into a
  race. `AppiumGuard.SelectDefaultFolder(expectImages: 0)` also had to start asserting the empty
  label's *text*, since the loading caption now satisfies its mere presence.
- **Unrelated bug found and fixed in passing:** the source-shape contract tests only passed on an LF
  checkout. `core.autocrlf` is on, so a Windows working tree is CRLF, and .NET's multiline `$` anchors
  before `\n` only — never before `\r\n` — so `MethodBody`'s declaration regex silently matched
  nothing and 8 tests failed. `SourceShape` normalises line endings on read.

### Found in review, after the first working version

A five-lens review of the finished change caught these. Recorded because each was a *silent* failure
— the app worked, the suite was green, and nothing pointed at them.

- **A stale load's failure could destroy a live one.** The abandonment guard covered only the success
  path; `LoadAsync`'s catch rethrew unconditionally, so a superseded load that threw wiped the pool,
  captioned a folder that was fine as unopenable, and abandoned the load about to replace it. The
  fix makes abandonment total: a load nobody is waiting for reports nothing, failures included.
- **The loader held the Activity's `ContentResolver`**, which reaches back to the Activity through
  its `ContextImpl` — so a walk in flight pinned the destroyed screen's whole view tree, defeating
  the point of the `static` worker. It takes `ApplicationContext.ContentResolver` now.
- **A grant revoked while stopped left the screen inconsistent**: `OnStop` released the grid but kept
  the pool, and `OnStart` returned silently with nothing to restore — empty grid, previous folder's
  count, Start armed over images that no longer resolve. Exactly what `ResetLibrary`'s own comment
  says must not happen.
- **Same-folder retention compared document ids, not tree uris.** `primary:Pictures` is not unique
  across providers, so a same-named folder on an SD card or cloud provider kept the previous
  provider's pool armed under the new folder's name.
- **Three contract assertions were unfalsifiable**, including the primary pin for `INV-X-13`: it
  searched for the substring `generation`, whose first hit is the ticket being *taken*, so the
  ordering held however the guard was mangled. Deleting the guard outright left it green.
- **The return-from-session test could not fail for the bug it existed to catch** — it
  waited on the pool count, which same-folder retention keeps populated from the moment `OnStart`
  runs. It now waits on the *tiles*, and asserts the session actually started first.
- **The Appium repairs missed the no-argument `SelectDefaultFolder()`**, used by the whole player
  suite, which returned before the load landed and then asserted Start was enabled.

### Found in a second review round

The same five lenses run again over the fixed code. What they caught:

- **The abandonment guard had one unguarded exit** — a tree uri with no document id returned a
  result without checking the ticket, so a superseded load could still clobber a newer pool with an
  empty one.
- **`OnStart` keyed "did I have a folder?" off the pool**, which is empty while a load is in flight.
  A load abandoned before it landed therefore left the pane reading "Reading that folder…" forever.
  It keys off `loadedTreeUri` now, which is set the moment a folder is chosen.
- **A revoked grant was captioned as an error.** `INV-GRP-5` calls an expired grant an expected
  outcome; captioning it "Couldn't open that folder" makes it indistinguishable from a broken
  provider. It resets to the empty state instead.
- **`RememberedTree()` was unguarded on a path that now runs constantly.** It enumerates the
  platform's persisted grants — a binder call — and `OnStart` runs it on every return, so a fault
  would have been a crash on every foreground, off a persisted reference (`INV-X-11`).
- **`INV-X-13` had no test that executed any code.** The abandonment arithmetic moved into Core as
  `LoadGeneration` with `LoadGenerationTests`; source-shape tests cannot tell a working comparison
  from an inverted one, which is exactly the mutation the contract tier kept surviving.
- **Several contract assertions were still unfalsifiable** — the failure-path guard could be reduced
  to a log and a rethrow, the same-folder clear could be hoisted out of its `if`, and neither
  `isCurrent()` check pinned its polarity. All now bound to the branch they belong to.
- **Two Appium assertions were wrong in different ways**: the terminal-state wait was satisfied by
  the pool the re-walk deliberately retains, and an exact tile count depends on the device profile
  because the grid scrolls and UiAutomator reports only what is on screen.
- **JNI peers on the walk** (the child-documents uri, the cursor, the per-image uri) were left to a
  finalizer pass, against the same ceiling the round-one fix cited one closure away.

### Found in a third and fourth review round

The rounds converged: no Critical findings after the first, none Important after the third, and the
fourth came back clean on security. What the last two caught:

- **The picker was a third path through `OnStop`/`OnStart`.** Its result arrives *after* `OnStart`,
  while `Settings.LastCollection` still names the old folder — so every pick started a full walk of
  the folder being replaced, which the result then superseded. `awaitingPickResult` defers the
  rebuild to the result, and a cancelled pick restores the grid `OnStop` released.
- **The walk's abandonment check was per query, not per row.** For the app's usual shape — one flat
  folder of thousands of images — those are the same thing, so an abandoned load drained the whole
  cursor before it could stop.
- **`ReferenceLibrary.Enumerate()` was public with no production caller.** A method whose contract is
  "never call me" is a rule enforced by prose; it is private now, and a re-walk is a fresh instance.
- **The pool-retention comparison had the same problem `LoadGeneration` did** — a decision no
  source-shape test can tell from its own inverse. Extracted to Core as `LibraryLoadState`.
- **An unreadable tree was reported as an empty folder**, telling the artist their folder holds no
  images when it could not be opened.
- **The empty state could not distinguish "no folder" from "folder with nothing in it"**, so an
  expired grant read as an empty folder.
- **Several assertions were bound by position rather than by nesting**, and would have survived the
  statement being hoisted out of the branch it guards. `SourceShape.BlockAfter` now refuses a brace
  that does not open the guard's own block, and is itself tested.

### Not verified automatically

Two acceptance criteria no tier can reach. They need a human with a real device:

- A real ~2,000-image folder: the setup pane paints immediately, the library pane shows the loading
  caption, then the count. This is *the* criterion of the ticket, and only a real library shows it —
  the emulator harness seeds 70-byte images.
- `adb shell dumpsys meminfo` during a session, confirming the grid's 24 previews are not resident
  under the player. That is the entire point of moving the release to `OnStop`.
