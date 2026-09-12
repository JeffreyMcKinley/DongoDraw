# Part II — Domain model (DDD view)

Sections 1–14 of [ARCHITECTURE.md](ARCHITECTURE.md) describe the *physical* architecture: which project a type lives in and what it may
reference. Sections 15–21 here describe the *domain* architecture: what the app is about, which
concepts belong together, and which rule each type is allowed to own. The per-object rules and
numbered invariants live in a companion document, [DOMAIN-MODEL.md](DOMAIN-MODEL.md). The two views must agree —
a bounded context that cannot live inside `DongoDraw.Core` is a modelling mistake, not a
reason to put a rule in an Activity.

Derived from the code as of #4, following the `v3-ddd-architecture` skill. Two of that
skill's prescriptions are **deliberately not adopted**: the microkernel/plugin runtime and a
dependency-injection container. This is a single-user offline app of ~2,000 lines with no
extension points and no third-party modules; a kernel registry would add indirection without
removing a rule. Constructor injection by hand (§4) already gives the same inversion.

## 15. Ubiquitous language

The words below are the only correct names for these concepts. Code, tickets, log messages, and
strings use them consistently; a synonym in a new type name is a review comment.

| Term | Means | Not |
|---|---|---|
| **Reference image** | One picture the artist draws from, identified by a SAF content URI string | "photo", "file" |
| **Reference library** | The picked folder plus every drawable image discovered beneath it | "group", "collection", "album" |
| **Pool** | The ordered image ids a library hands to one session | "gallery", "album" |
| **Pass** | One traversal of the whole pool; a new pass reshuffles. Pools smaller than the target count repeat by passes | — |
| **Pose** | One display of one reference image for the configured duration | "slide", "frame" |
| **Session** | A run of N poses under one config, from Start to completion or early end | "workout", "practice" |
| **Setup** | The pre-session screen state: folder + seconds-per-image + count, and whether Start is allowed | "config screen" |
| **Config** | The validated `(SecondsPerImage, ImageCount)` pair handed to a session | "settings" (that's persistence) |
| **Complete** | A pose that counted toward the target — timer expiry or a manual done-tap | "finished" (ambiguous with session end) |
| **Skip** | Leaving a pose without counting it and without banking its time | "next" |
| **Break** | The configured rest between two poses. Never before the first or after the last | "pause" (that's the drawer stopping the clock) |
| **Viewing aid** | Grayscale, flip, grid, blur, zoom — how the pose is presented, never what it counts | "filter", "effect" |
| **Drawing time** | Accumulated time over completed poses only; skipped time is never banked | "elapsed", "session length" |
| **Unreadable** | An image id the loader could not decode; skipped and logged, never fatal | "corrupt", "missing" |
| **Settings** | The persisted user preferences document that *seeds* setup across launches | "config" |
| **Load** | One attempt to read a reference library: the walk plus the previews it decodes | "refresh", "scan" |
| **Abandon** | Declare a load's result unwanted, so it writes nothing and frees what it decoded | "cancel" (nothing is interrupted; the load notices and stops) |
| **Preview** | A decoded thumbnail in the reference grid, as against the pose on the player screen | "thumbnail" in prose — the identifiers keep it (`MaxThumbnails`, `thumbnail_desc`) |

Two distinctions carry real invariants and are worth stating twice:

- **Config vs Settings.** `SessionConfig` is validated, immutable, and scoped to one session.
  `Settings` is persisted, mutable, and scoped to the installation. Settings seed setup; setup
  produces config; config never writes back to settings implicitly.
- **Complete vs Skip vs End.** Three different terminal moves on a pose, with three different
  effects on `CompletedCount` and `TotalDrawingTime`. See the invariant table in §17.

## 16. Bounded contexts

Four contexts, each a cohesive vocabulary with its own rules. All four live in
`DongoDraw.Core`; the Android layer holds only their adapters and screens.

| Context | Owns | Core types today | Namespace / folder |
|---|---|---|---|
| **Reference Library** | Discovering drawable images under a picked folder; what counts as an image; the pool; whether the remembered folder is still usable | `ReferenceLibrary`, `IDocumentTree`, `DocumentEntry`, `LibraryReference`, `PersistedGrant` | `DongoDraw.Core` (root) |
| **Session Setup** | Parsing and validating the two inputs; the Start gate; producing a config | `SessionSetup`, `SessionConfig`, and the session's `Draft` phase | `DongoDraw.Core` (root) |
| **Session Execution** | Running a session: sequence, passes, counts, skip semantics, time accounting, per-pose countdown, breaks, resolving an id to a displayable image, the totals, the viewing aids | `DrawingSession<TImage>`, `ViewerTools` | `DongoDraw.Core/Session` |
| **Preferences** | The persisted settings document and its lifecycle | `Settings` | `DongoDraw.Core/Data` |

Supporting, deliberately outside the four: **Rendering** (`ImageDecoding`, `BitmapMath`,
`GridContrast`, the `ImageView` wiring). It has no domain rules — only the memory-bound decode
policy of §8 and the legibility policy of the viewing aids. It is a shared technical service, not a
context, and `BitmapMath` and `GridContrast` are the pieces of it pure enough to live in Core.

A second supporting group since #4: **Background work** (`LibraryLoader`, and in Core
`LoadGeneration` and `LibraryLoadState`). Also no domain rules — it decides which in-flight load may
write the screen and whether a re-walk keeps the pool it is showing (`INV-X-13`). The two Core
pieces are there because they are the parts a test can execute; the loader stays Android-side
because it holds decoded bitmaps.

`GridContrast` is where a viewing aid's *appearance* is decided, as against `ViewerTools`, which
owns whether that aid is on. Keeping the two apart is what lets `ViewerTools` stay a bag of pure
flags (`INV-VIEW-3`): the screen samples the decoded pose down to a small block of pixels once per
pose and asks `GridContrast` which tone each guide takes, and neither the block nor the answer is
ever session state.

**Totality is `GridContrast`'s contract, not an implementation detail.** Every degenerate input —
no samples, a span shorter than the grid it claims, a zero-sized stage or image, a non-positive or
non-finite zoom — returns four light styles rather than throwing. It is called from the render path,
where an exception is a screen that stops rather than a frame that looks wrong, and the light style
is the one that reads over `@color/stage`, which is also where a guide lands when the pose is
letterboxed away from it. A guide with the wrong tone is a bad frame; a guide that throws is a dead
session.

### Context map

```
        Preferences  ──seeds──▶  Session Setup  ──SessionConfig──▶  Session Execution
             ▲                        ▲                                    ▲
             │                        │                                    │
        (last folder)          folderSelected                            pool
             │                        │                                    │
             └────────────  Reference Library  ─────────────────────────────┘
                                     ▲
                                     │ IDocumentTree (anti-corruption layer)
                                     │
                          Storage Access Framework (Android)
```

Relationship types, using the standard names, because each one implies a different rule for
change:

- **Session Setup → Session Execution — customer/supplier, published language.** `SessionConfig`
  is the contract. Execution never re-validates it and never reaches back into setup state. A new
  session input is a new field on `SessionConfig`, added at both ends in one change.
- **Reference Library → Session Execution — supplier.** The pool crosses as
  `IReadOnlyList<string>`. Execution treats ids as opaque: it never parses, sorts by, or
  interprets a content URI. That opacity is what lets tests pass `"a"`, `"b"`, `"c"`.
- **Storage Access Framework → Reference Library — anti-corruption layer.** `IDocumentTree` +
  `DocumentEntry` are the ACL. `DocumentsContract`, `ContentResolver`, and `Cursor` stop at
  `LibraryLoader.ContentResolverDocumentTree` — that is §4's interface-adapter pattern stated as a
  context rule. One carve-out, stated rather than drifted into: the *form* of the reference this app
  persisted is domain knowledge, so `LibraryReference` recognises a `content://` tree URI while
  still never constructing, resolving, or querying one (`INV-TREE-1`, `INV-REF-1`).
- **Preferences → Setup — open host, one direction.** Settings seed the inputs and record the last
  folder. Neither Session Setup nor Session Execution reads `Settings` at runtime; the Android layer
  copies the values it needs into intent extras at launch (§5).
- **Screen → screen — serialization boundary.** Intent extras are the wire format between
  `MainActivity` and `SessionActivity`. They carry primitives only, keyed by the public constants
  on `SessionActivity`. That boundary is why a `SessionConfig` is reconstructed on the far side
  rather than passed as an object.

## 17. Tactical model

### Session Execution

**Aggregate root: `DrawingSession<TImage>`.** It is the consistency boundary for everything about
the run — the upcoming queue, `CompletedCount`, the drawing-time total and both clocks are only ever
mutated through `Next` / `Skip` / `End` / `Tick` / `Pause(PauseReason)` / `Resume` — with one stated
exception, `UpcomingImageId`, which may refill a drained pass in order to answer (`INV-PLY-7`) and
changes nothing else — and no caller can observe them
mid-transition. Its identity is positional (one live session per player screen), so it carries no
id: a session is never stored, never compared, and never resumed. That is the reason it has no
repository, and adding one would be the signal that the model changed, not a convenience.

| Element | Kind | Note |
|---|---|---|
| `DrawingSession<TImage>` | Aggregate root, entity | Owns the draft, the sequence, the counts, both clocks, the break, image resolution and the totals |
| `SessionConfig` | Value object (`readonly record struct`) | Immutable, validated upstream |
| `SessionPhase` | Enum | `Draft` → `Pose` ⇄ `Break` → `Complete` |
| `PauseReason` | Enum | Why the clocks stopped: `Lifecycle` (screen hidden) vs `User` (the drawer asked). `INV-CD-8` |
| `SessionTick` | Enum | What a tick did: `None`, `PoseStarted`, `BreakStarted`, `Completed`. A return value, not a concept with a lifetime. `INV-SES-13` |
| `ViewerTools` | Entity | Owns the viewing aids and the zoom range; touches nothing the session counts |
| image id (`string`) | Primitive standing in for a value object | See "candidate: `ImageRef`" below |

**Invariants the aggregate enforces.** These are the rules a change must not break; each has a
test in the `DrawingSession*Tests` files, which are split by invariant family (§11).

| Invariant | Enforced by |
|---|---|
| `Remaining` is never negative; `CompletedCount` never exceeds `TargetCount` | `Next` finishes at the target; `Remaining` clamps |
| Every image is shown once before any repeat | `Refill` rebuilds a full pass before dequeuing |
| A tick reports the transition it made | `Tick` returns `SessionTick`, read off the phase it left behind |
| The next id can be asked for without consuming it | `UpcomingImageId` peeks the queue; refilling a drained pass is its one mutation |
| An unreadable image is loaded once per session | `Resolve` consults `_unreadable` before calling the loader |
| Skip never advances `CompletedCount` and never banks time | `SkipCurrent` advances the sequence; time is banked only in `CountCurrent` and `End` |
| `End` banks the current partial time but does not count the pose | `End` accumulates, then `Finish` |
| Drawing time excludes all skipped time | Time is banked only in `Next` and `End` |
| Drawing time excludes break, background and paused time | `Pause` stops both clocks; the session clock stays stopped for the whole break |
| Every operation is a no-op once `IsComplete` | Guard at the top of `Next` / `Skip` / `End` |
| An empty pool or a zero count completes immediately rather than hanging | `Advance` finishes when `_targetCount <= 0 \|\| _pool.Count == 0` |
| Time is monotonic and injectable | `Func<TimeSpan> clock`, never `DateTime.Now` (§4) |
| A skip raises `SkippedCount`, then lands on a fresh pose | `SkipCurrent` increments and advances; `StartPose` restarts the clock |
| A break never counts a pose, and never follows the last one | `CountCurrent` finishes at the target before `CompletePose` can enter a break; a done-tap during a rest just ends the rest |
| A skip lands on the next pose, never on a break | `Skip` calls `StartPose` directly |

**The pose clock is inside the root, not beside it.** It was once a separate `PoseCountdown`
entity, on the reasoning that its pause/resume lifecycle was its own. It is not: `INV-SES-12` stops
the drawing-time clock on exactly the edges that stop the countdown — pause, resume, break — so two
objects were being driven in lockstep by a third. One aggregate with two private clocks removes the
lockstep without weakening a rule (DOMAIN-MODEL.md §9).

**Closed: the pose-restart rule.** "The pose clock restarts whenever the current image changes" used
to be enforced by `SessionActivity.Advance` (`player.Next(); countdown.Restart();`) — a state machine
inside an Activity, and one the skip control would have had to repeat. It now lives inside the
session, which exposes `Next` / `Skip` / `End` / `Tick` / `Pause` / `Resume` and leaves the Activity
with repaint, rendering and lifecycle. Adding the between-poses break is what forced the issue: the
pairing became a three-state machine (pose → break → pose), which is not something a screen may own.

`DrawingSessionBreakTests` asserts the pairing directly, and `SessionScreenContractTests` asserts
the negative — that `SessionActivity` constructs exactly one session object and no clock of its
own.

**Candidate: `ImageRef` value object.** Image ids are bare `string`s throughout. A one-field
`readonly record struct ImageRef(string Value)` would make "opaque id" enforceable rather than
conventional. Worth doing only if a second string-shaped concept enters the same signatures;
today it would be ceremony.

### Reference Library

`ReferenceLibrary` is the **aggregate root**: the picked folder's identity, the depth-first walk
beneath it, and the pool that walk produces. The classification rules (`IsImage`, `IsDirectory`)
stay static and pure — they are domain knowledge belonging to no instance. `IDocumentTree` is a
**port** and `DocumentEntry` a **value object**, kept outside the aggregate because an
anti-corruption layer that lives inside the thing it protects is not one.

Invariants: traversal is depth-first in encounter order, termination is guaranteed twice over (the
`visited` set stops a reported cycle, a depth ceiling stops a provider that synthesizes a fresh id
per level), and ids are de-duplicated so one document reached twice is one pool entry. The pool
leaves the context as `IReadOnlyList<string>`; the library keeps the root *document id* alongside it,
which is what FD-008's "which folder am I drawing from" display needs. The tree URI itself stays in
the Android layer, which is what writes `Settings.LastCollection`.

### Session Setup

A static domain service (`SessionSetup`) for parsing, validity and pacing, plus one value object
for the output (`SessionConfig`). The evaluated state of the screen is not a third type: it is a
`DrawingSession<TImage>` in its `Draft` phase, because a session that has not started yet is exactly
what the setup screen is showing. The Start gate (`CanStart`) is a domain rule, not a UI rule — the
Android layer binds a button's `Enabled` to it and owns nothing else. Parsing lives here too
(`ParsePositive`), which is correct: "what counts as a valid seconds input" is domain knowledge, and
keeping it out of the `EditText` handler is what makes it testable.

The cost of that merge is one odd-looking call: `MainActivity` says
`DrawingSession<PoseImage>.Evaluate(...)` on a screen that never touches a bitmap. That was the
accepted trade for deleting a type whose only job was to hold four fields.

### Preferences

`Settings` is an **aggregate root** with a fixed identity (`Id == 1`) that owns its own persistence:
`Open` (create-on-first-read), `Save` (upsert), `Dispose`. Document and repository were two types;
with one document, one collection and one store forever, the split bought a name and no seam.

Two deviations worth naming, neither urgent:

- **No port interface.** Core's persistence is a concrete class, so any future Core consumer would
  depend on LiteDB rather than on an abstraction. Today only Activities call it, so there is no
  second implementation to justify the seam; introduce `ISettingsRepository` at the moment a Core
  type needs to read settings — that is also the moment the document/repository split earns its keep
  again.
- **Persistence attribute on the domain entity.** `Settings` carries LiteDB's `[BsonId]`, so the
  storage technology is visible on the model. Acceptable at one document and one collection; if it
  grows domain behaviour, split it into a domain type and a persisted DTO rather than spreading BSON
  attributes.

## 18. Domain events

**There is no event bus today, and that is the right call at this size.** Communication between
contexts is direct calls and observed state: the repaint loop calls `Tick` and repaints; the screen
reads `CurrentImage`, `Display`, `IsComplete`, `CouldNotDisplayImage`. One
producer, one consumer, same thread — a publisher/subscriber layer would add indirection between
two objects that already know each other.

The events below are nevertheless **named**, because the names are the vocabulary FD-006/007 will
use in tickets, logs, and analytics whether or not a type exists:

| Event | Raised when | Consumed by |
|---|---|---|
| `PoseCompleted(imageId, duration)` | Timer expiry or manual done-tap | Counts, summary |
| `PoseSkipped(imageId)` | FD-006 skip control, or an unreadable image | Logging; never counts |
| `ImageUnreadable(imageId)` | Loader returned null | `onUnreadable` hook -> `Log.Warn` |
| `SessionCompleted(summary)` | Target count reached | FD-007 summary screen |
| `SessionEndedEarly(summary)` | `End()` called | FD-007 summary screen |
| `PoolUndisplayable` | Consecutive-failure budget exhausted | Error state |

Two of these already exist in weaker form: `ImageUnreadable` as the injected `Action<string>?
onUnreadable` callback, and the terminal ones as the `IsComplete` / `CouldNotDisplayImage` flag
pair that `SessionActivity.Render` branches on.

**If events do become worth materializing** — the trigger is a *second* consumer, e.g. session
history persistence or streak tracking landing alongside the summary screen — do it as an
in-aggregate list, not a bus:

```csharp
public interface ISessionEvent;
public readonly record struct PoseCompleted(string ImageId, TimeSpan Duration) : ISessionEvent;

// on the aggregate root
public IReadOnlyList<ISessionEvent> DrainEvents();   // returns and clears
```

The screen drains after each command and dispatches. That keeps the events testable in Core, keeps
ordering deterministic, and adds no threading question. A static event bus would also violate §5's
"no statics or singletons for cross-object handoff".

## 19. Clean architecture layers

The skill's four layers map onto the projects of §2 as follows. Dependencies point inward only.

| Layer | Contains | Lives in |
|---|---|---|
| **Presentation** | `MainActivity`, `SessionActivity`, layouts, strings | App project |
| **Application** | Use-case orchestration: wiring a session, advancing a pose, launching a screen | Mostly inside the Activities; resolving an id to a displayable image now sits inside the domain aggregate |
| **Domain** | `DrawingSession<TImage>`, `SessionSetup`, `ReferenceLibrary`, `ViewerTools`, value objects | `DongoDraw.Core` |
| **Infrastructure** | `Settings` (LiteDB), `LibraryLoader` + its `ContentResolverDocumentTree` (SAF, off the UI thread), `ImageDecoding` (BitmapFactory) | Core `Data/` + app project |

The domain layer has no outward dependency: `DongoDraw.Core` references only LiteDB, and only
from `Data/`. Verified structurally by `AndroidBuildTests` and by the project references.

**The application layer is the blurry one, on purpose.** `SessionActivity.OnCreate` composes the
object graph and the repaint loop calls `Tick` — application-layer work living in a
presentation-layer class. At this size that is an accepted trade (§3: no ViewModel layer). The
threshold for extracting it is stated in §17: when the same multi-object sequence appears in two
screens or two handlers, it moves to Core. The consolidation moved one such sequence already —
resolving an id to an image is no longer a separate service the screen wires up.

## 20. Where the code deviates today

Live findings, ordered by how much they cost. None is a blocker; each has a stated trigger.

1. **`MainActivity` spans three contexts** — Reference Library (SAF picking, the library, the
   thumbnail grid), Session Setup (inputs, preset chips, Start gate), Preferences (opening the
   database, loading and saving settings). The Claude Design import made this literal: the three
   contexts are now the three tabs of one screen. Not a god object at this size, but it is the only
   class in the codebase that touches three contexts, so it is where the next rule will be tempted
   to land.

   *Deferred once, deliberately (remembered-folder work).* That change added `OnPause`,
   `SaveSettings`, `CaptureTypedInputs`, `RememberedTree`, `LastPickedDocumentUri`,
   `PersistedGrants`, `ReleaseSupersededGrants`, `RefreshGrant` and `ShowRememberedFolderUnavailable`
   to this class, and moved every *rule* it could into `LibraryReference` instead of splitting the
   screen.

   **Partly closed by #4**, which took the first split: the folder walk, the preview decodes,
   the SAF adapter and the abandonment guard now live in `LibraryLoader`, and the Activity keeps
   view wiring and lifecycle. Settings-syncing is the remaining candidate, and the trigger stands —
   the next feature that adds a method here which is neither view wiring nor a one-line call into
   Core does that split first.
2. ~~The pose-restart rule lives in an Activity~~ — closed by the session aggregate (§17).
3. ~~`SettingsStore` has no port interface~~ — closed by merging it into `Settings` (§17): there was
   no second implementation to justify the seam. `Settings` still carries a LiteDB attribute, and
   the trigger for splitting a domain type back out is stated there.
4. **Session state is not persisted across process death** (§5) — from a DDD angle, the session
   aggregate has no identity and no repository, which is exactly why rotation restarts a pose. If
   FD-008's rotation handling requires survival, that is the point at which `DrawingSession` gains
   an id and a snapshot/restore pair, not a `static`.
5. **The draft phase is generic for no reason of its own** (§17) — `DrawingSession<PoseImage>.Evaluate`
   on a screen with no bitmaps. Harmless, and cheaper than keeping a type to avoid it, but it is the
   one place the merged model reads worse than what it replaced.
6. **Zoom carries across poses.** `ViewerTools.ResetZoom` exists and nothing calls it, so a 2.5×
   zoom set for one pose is still applied to the next. `INV-VIEW-4` was written to match the code
   rather than the other way round; wiring the reset into the phase change is a one-line UX decision
   nobody has made.
7. ~~**Image decoding runs on the main thread**~~ — closed on both sides. The player screen's half
   went with [#5](https://github.com/JeffreyMcKinley/DongoDraw/issues/5) (the next pose is decoded during the
   current one), and the folder walk's half with
   [#4](https://github.com/JeffreyMcKinley/DongoDraw/issues/4) (the walk and up to 24 preview decodes moved off
   the UI thread). Grants are no longer accumulated either: picking a different folder releases the
   ones it supersedes and a restore re-takes the one in use (`INV-REF-4`).

   Three entries that used to sit here are closed: the pool no longer crosses to the player whole
   (it is sampled to a handoff bounded by the session's own length, `INV-POOL-6`), decoded bitmaps
   are recycled by the screen that decoded them, and the player screen no longer decodes on the UI
   thread at all — the next pose is decoded during the current one, the session's construction (and
   with it the first pose) happens off the thread too, and an unreadable file is loaded once per
   session rather than once per pass (`INV-PLY-7`, `INV-PLY-8`, #5).

   What is left there, stated precisely because "no longer decodes on the boundary" is easy to
   over-claim: a boundary that arrives mid-decode *waits* for the decode it already started, and a
   boundary that skips past an unreadable id decodes the replacement itself, because only one image
   is ever decoded ahead. Both are bounded — the first by what remains of a decode in progress, the
   second by one image — and neither is the unbounded run the old code had. The failure budget is
   also now the player's explicit `pool.Length * 2` rather than the implicit 100: with `INV-PLY-8`
   the *decodes* in a hopeless run are capped by the pool rather than by the budget, but the budget
   is larger than it was, so a wholly unreadable folder does more work before the error screen than
   it used to. It does that work off the UI thread.

## 21. Testing the model, and what "done" looks like

The four tiers of §11 map cleanly onto the model, and the mapping is the rule for where a new test
goes:

- **Aggregate invariants** (the §17 table) → unit tests, one file per Core type, with injected
  clock and `Random`. Every row of that table is a test.
- **Cross-context flows** (setup → session → summary) → the `*E2ETests.cs` model tests, which drive
  the real Core objects with no Android.
- **Adapter conformance** (`IDocumentTree`, the bitmap loader) → in-memory fakes in unit tests. The
  Android implementations are covered by the contract tests for their view ids and strings, and —
  since #4 — for the threading and ordering decisions a unit test cannot reach: which lifecycle
  method abandons a load, and that the guard is read before anything is written.
- **Nothing about the domain is tested through Appium.** A domain rule reachable only from a UI test
  is a rule in the wrong layer (§14).

Success criteria for the DDD structure, checkable rather than aspirational:

- [x] `DongoDraw.Core` has zero `Android.*` / `Java.*` references — guarded by project setup and `AndroidBuildTests`
- [x] Each Core type belongs to exactly one context in the §16 table, or to a named supporting group there; new types are added to it
- [x] The catalogue stays at ten objects unless a new one earns its place — a new Core type must
      justify itself against [DOMAIN-MODEL.md §9](DOMAIN-MODEL.md#9-consolidation), or be added to
      the catalogue with its own invariants and tests
- [ ] Context dependencies stay acyclic and match the §16 map — Execution never reads settings, Setup never reads the pool's contents
- [ ] Every invariant in §17 has a named unit test
- [x] No aggregate mutates through a public field or a setter — commands only
- [ ] No rule reachable only through an Activity (the §14 list, plus the §20 findings closed)
- [x] Any new time or randomness in Core arrives through an injected `Func<TimeSpan>` / `Random`
