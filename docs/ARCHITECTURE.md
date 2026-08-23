# Architecture — FigureDrawing

How this codebase is organized and the rules changes must hold to. Written for agents and
contributors reviewing or extending the app. Product scope lives in the root [README](../README.md);
planned work lives in [docs/prds](prds/README.md).

> Derived from the code as of FD-005. Where a rule below is marked **(confirm)** it was inferred
> from existing code rather than stated anywhere — correct it if the intent differs.

## 1. The one rule

**All logic that can be written without Android goes in `FigureDrawing.Core`. The Android layer
only wires it to views.**

Everything else in this document follows from that. Core is a plain `net9.0` library with no
Android reference, so every rule in it is reachable by a fast unit test on the desktop runner.
The Android projects need a device or emulator, so anything that lands there is expensive to
verify and stays deliberately thin.

## 2. Projects

| Project | TFM | Role |
|---|---|---|
| `FigureDrawing.csproj` (repo root) | `net9.0-android` | Android app: Activities, layouts, resources, decoding |
| `FigureDrawing.Core` | `net9.0` | Session engine, setup validation, folder enumeration, settings persistence, bitmap math |
| `FigureDrawing.Tests` | `net9.0` | xUnit unit, E2E-model, and contract tests. No device needed |
| `FigureDrawing.UITests` | `net9.0` | Appium UI tests. Needs a running emulator |

Dependency direction, and the only direction allowed:

```
FigureDrawing (Android)  ──▶  FigureDrawing.Core  ──▶  LiteDB
      │                              ▲
      │                              │
FigureDrawing.UITests          FigureDrawing.Tests
   (via Appium, black box)        (direct reference)
```

**Rules**

- `FigureDrawing.Core` must never reference `Mono.Android`, `Android.*`, or `Java.*`. If a Core
  type needs a platform concept, it takes an abstraction or a delegate instead (see §4).
- The app project references Core. Core never references the app.
- Nothing references the test projects.
- LiteDB is a transitive dependency of the app via Core — do not add a direct `PackageReference`
  for it to the app project.

**Root-glob gotcha.** The app csproj sits at the repo root, so its default `**/*.cs` glob would
swallow the sibling projects' sources. The three `<Compile Remove>` entries in
`FigureDrawing.csproj` are load-bearing; `AndroidBuildTests` guards them. Adding a new sibling
project means adding a fourth exclude.

## 3. Layers

### Core — pure model

| Type | Owns |
|---|---|
| `SessionSetup` / `SessionConfig` | Parsing, validity, the presets, and how long a configured session runs |
| `DrawingSession<TImage>` | The session aggregate: the draft (inputs, Start gate, estimate), the sequence, the pose clock, the break, resolving an id to a displayable image, and the totals |
| `ViewerTools` | Grayscale/flip/grid/blur flags and the zoom range for the pose on screen |
| `ReferenceLibrary` / `IDocumentTree` / `DocumentEntry` | The picked folder, the recursive image discovery beneath it, and the pool |
| `LibraryReference` / `PersistedGrant` | Whether the remembered folder is still worth acting on, and whether its read grant is still held |
| `BitmapMath` | Power-of-two sub-sample calculation |
| `GridContrast` | Which tone each rule-of-thirds guide takes from the pose under it |
| `Data/Settings` | The persisted settings document and its LiteDB store |

Ten catalogued objects, nine of them Core types today (`SessionRecord` is still proposed),
deliberately: what each one is and why the neighbours it absorbed are not
separate concepts is [DOMAIN-MODEL.md §1](DOMAIN-MODEL.md) and [§9](DOMAIN-MODEL.md#9-consolidation).

Core types are deterministic and side-effect free apart from `Settings`. They expose state as
properties for the screen to read (`CurrentImage`, `Remaining`, `IsComplete`, `Display`) and accept
commands as methods (`Next`, `Skip`, `End`, `Tick`, `Pause`, `Resume`). Starting a session is the
running constructor, not a command — there is no public `Start` and no public `Restart`; restarting
the clock is internal (`RestartCountdown`).

### Android — screens only

| Type | Owns |
|---|---|
| `MainActivity` | The three tabbed panes: setup inputs, the reference library + folder picker (SAF), settings |
| `SessionActivity` | The player screen: pose, rail, break/pause overlays, summary, the repaint loop, lifecycle |
| `ImageDecoding` | Two-pass `BitmapFactory` decode shared by both screens |

The look is the **Nocturne** design system, imported from the Claude Design project *Figure Drawing
Practice App*. Its tokens live in `Resources/values/colors.xml` + `dimens.xml`, and its component
classes (`.tool-chip`, `.btn-*`, `.card`, `.input`, the tab bar) are the widget styles in
`Resources/values/styles.xml`. Retune the system there rather than styling a control inline; a
one-off `android:background` on a button is the same kind of violation as a rule in an Activity.

**Typeface.** The system's font, Inter, is bundled at `Resources/font/` (weights 400/500/600, the
same three the design system imports) under the SIL Open Font Licence — see
`docs/third-party-licenses/`. It is what puts the app's minimum at **API 26**: framework font
resources arrived in Oreo. It reaches views through the theme's `android:textAppearance` and
`android:textAppearanceButton`, *not* through a bare `android:fontFamily` item on the theme —
`TextView` never reads that off the theme, so setting it there looks right and does nothing.
`EditText` is the one exception and names the family in the `Input` style, because the platform's
`Widget.Material.EditText` pins its own text appearance. `TypefaceContractTests` guards all of it.

An Activity is allowed to: find views, read intent extras, subscribe to view events, call Core,
render Core's state, and manage its own lifecycle. It is not allowed to own a rule.

**(confirm)** There is no MVVM/MVP framework here and no ViewModel layer — screens talk to Core
objects directly. That is the intended shape at this size; introducing a presenter layer is a
deliberate decision, not a drive-by refactor.

## 4. Crossing the boundary

Core never imports Android. Where it needs something the platform provides, it takes it as a
constructor parameter. Three patterns already in use — reuse them rather than inventing a fourth:

One carve-out, stated so it is not mistaken for drift: the *form* of the persisted reference —
that it is a `content://` tree URI — is domain knowledge and lives in `LibraryReference`. Core
recognises that shape and never constructs, resolves, or queries one; `DocumentsContract`,
`ContentResolver`, `Cursor` and `Uri` still stop at the adapter.

**Interface adapter.** `IDocumentTree` describes "list the children of a folder document".
`MainActivity.ContentResolverDocumentTree` backs it with `DocumentsContract` + `ContentResolver`;
tests back it with an in-memory tree.

**Injected delegate.** `DrawingSession<TImage>` takes `Func<string, TImage?> load`. The screen
passes `LoadPose`; tests pass a fake. The generic parameter is what keeps `Bitmap` out of Core.

**Injected clock.** `DrawingSession<TImage>` takes an optional `Func<TimeSpan>? clock`, defaulting
to a `Stopwatch`, and drives both its clocks — the drawing-time total and the pose countdown — from
it. Tests drive time deterministically. Any new time-dependent Core type must follow this — never
call `DateTime.Now` inside Core.

`Random` follows the same rule: the session takes an optional `Random` so shuffles are reproducible
under test.

## 5. State and navigation

- **Session state** lives in the single `DrawingSession<PoseImage>` owned by `SessionActivity`, created
  in `OnCreate` from intent extras. The setup pane holds no session: it evaluates a *draft* one per
  keystroke (`DrawingSession<PoseImage>.Evaluate`), which copies no pool and starts no clock.
- **Screen-to-screen handoff** is intent extras only. `SessionActivity` declares its keys as public
  constants (`ExtraPool`, `ExtraSeconds`, `ExtraCount`, `ExtraBreak`, `ExtraShuffle`,
  `ExtraGrayscale`, `ExtraKeepAwake`, `ExtraChime`); `MainActivity.StartSession` fills them. Add a new input by adding a constant, not a string literal
  at the call site, and not a static or singleton.
- **Persisted state** is the single `Settings` LiteDB document. It seeds the setup inputs on launch
  and records the last folder — which is both what a launch restores and where the picker reopens
  (`MainActivity.RememberedTree` / `LastPickedDocumentUri`). It is written where each value
  changes and again in `OnPause` (which first captures the typed inputs, the only values living
  nowhere else), since a swipe off the recents list never reaches `OnDestroy`. `Settings.Save`
  checkpoints, so a value that has been saved is in the datafile rather than only in the
  write-ahead log — see §6. `Android.Provider` also declares a `Settings`, so `MainActivity`
  carries a `using Settings = FigureDrawing.Data.Settings;` alias.
- **(confirm)** Session state is *not* currently saved in `OnSaveInstanceState`, so process death
  restarts the pose. That is a known gap, not a pattern to copy. `SessionActivity` mitigates the
  common case by declaring `ConfigurationChanges` for size/orientation and re-laying out in place,
  so folding or unfolding mid-session keeps the pose.

## 6. Data access

- LiteDB is reached only through `Settings`. No other type opens a `LiteDatabase`.
- The database file lives in app-private storage: `Path.Combine(FilesDir.AbsolutePath, "figuredrawing.db")`.
- `Settings` is `IDisposable` and is disposed in `OnDestroy`. It is a single-document store —
  `Id == 1` in the `settings` collection — opened with `Settings.Open(path)` and written with
  `Save()`. Saving through a disposed instance throws rather than dropping the write silently.
- `Save()` checkpoints before returning, so a save that returned is a save that survives a kill
  (`INV-STO-5`). LiteDB is write-ahead logged: an upsert alone leaves the value in `<name>-log.db`,
  and a process killed mid-write can leave that log truncated — which does *not* fail to open, it
  reads back as the values from before the save. That is a preference silently reverting with
  nothing thrown and nothing logged, and it is what "the app forgot my folder" looks like.
- `Save()` is a no-op when nothing has changed, so calling it on every pause costs nothing.
- New preferences are new properties on `Settings` with a default value. Do not add a second
  document or a second collection without a reason.

## 7. Threading

- Core is synchronous by design and never threads, sleeps or posts (`INV-X-9`). All background work
  in the app belongs to the player screen, which decodes reference images off the UI thread: the
  session's own construction (`BuildSession`, which resolves the first pose) and the next pose while
  the current one is up (`PrefetchUpcoming` / `DecodeAhead`). Everything else runs on the main
  thread.
- **One decode at a time per screen.** The prefetch holds a single slot (`prefetchTask`), and a
  request while it is occupied is dropped rather than queued — the slot re-aims itself when the
  decode settles. Without that bound, every command that changes which image is next starts another
  decode, and a few quick taps put several full-size decodes in flight at once.
- A boundary that arrives before the decode finishes **waits for it** rather than starting a second
  one. Blocking the main thread on a `Task.Run` that captured no synchronization context cannot
  deadlock, and the wait is bounded by what is left of a decode already in progress.
- Android UI objects may only be touched on the main thread. `SessionActivity`'s repaint loop uses
  `Handler(Looper.MainLooper)`, so it already is.
- The repaint `Handler` posts and removes **one stored `Java.Lang.IRunnable`**. Posting an `Action`
  wraps it in a fresh Java `Runnable` each call, so `RemoveCallbacks` would never match and ticks
  would survive teardown. Keep the stored-runnable pattern.
- Countdown time comes from a monotonic clock, never from counting ticks. A slow or dropped repaint
  must not change how much time a pose gets.
- Background work belongs behind an `async` method in the Android layer, and must be abandoned in
  `OnPause` and `OnDestroy`. `SessionActivity.PrefetchUpcoming` / `DecodeAhead` / `CancelPrefetch`
  are the worked example.
- **The stored-runnable rule above is about the repeating repaint queue, not about every callback.**
  A one-shot background result comes back through the `await` continuation, which Android's
  synchronization context posts to the same main looper. That continuation is deliberately *not*
  removable, and must not be made so: it is the code that frees a bitmap nobody wants any more, so a
  `RemoveCallbacks` that succeeded would strand the pixels with nothing left to release them. What
  makes it safe instead is a generation counter compared after the `await` — a result that lost its
  race is recycled rather than kept.
- **Never `ConfigureAwait(false)` in an Activity.** The continuation touches views and the fields
  that own bitmaps; resuming it on a pool thread makes every one of those a race. It is also the
  memory barrier that publishes decoded pixels to the thread that attaches them.
- A decode already running cannot be stopped, so "cancel" means "abandon the result", not "abort the
  work". A `CancellationToken` still earns its place: it drops work that has not started, and
  `ImageDecoding.DecodeSampledBitmap` checks it between its bounds pass and its decode pass, which is
  where the multi-megabyte allocation begins. Abandoning must not clear the slot — the work is still
  running, and a free slot would let the next repaint start a second decode beside it.
- The whole body of an `async void` method is wrapped, not just the `await`. What follows the await
  is JNI calls on peers that teardown may already have disposed, and an exception escaping there
  lands on the main looper with nothing above it to catch (`INV-X-11`).
- Work handed to the pool sets `ThreadPriority.Background` on entry and restores the previous value
  in a `finally`: a ThreadPool worker starts at normal priority and would compete with the UI thread
  it exists to protect, but it is shared, so it must not be left demoted.

## 8. Lifecycle and resources

- `OnPause` freezes the pose clock and stops the repaint loop; `OnResume` restores both, unless the
  drawer's own pause is still in effect (`INV-CD-8`). A backgrounded app must not burn pose time or
  fire a timer while hidden, and must not come back running from a pause the drawer asked for.
- `OnDestroy` stops the loop, clears `KeepScreenOn`, and disposes anything it owns.
- Every callback queued on the repaint `Handler` must be removable, and every listener attached to a
  long-lived object must be detached. The one exception is the one-shot `await` continuation of §7,
  which is deliberately not removable — it is the code that frees a bitmap nobody wants, so removing
  it would strand the pixels — and is made safe by a generation counter instead.
- Images are always decoded through `ImageDecoding.DecodeSampledBitmap`, never via `SetImageURI`.
  It picks a power-of-two `InSampleSize` (`BitmapMath.CalculateCropSampleSize`) from two rules: a
  request floor, which keeps the SHORT side at or above the requested size for centre-cropped tiles
  (360 px, `MainActivity.ThumbnailDimension`), and a ceiling, which holds the LONG side to within 2x
  the ceiling passed in — `MaxImageDimension` (1080 px) for a pose, `MaxThumbnailDimension` (720 px)
  for a preview tile — whatever the aspect ratio is. Power-of-two sampling is what leaves the 2x
  slop, so budget from twice the nominal dimension (a 4000x4000 photo decodes to 2000x2000); a
  12000x900 panorama decodes at 1500x112 rather than at full width. Never decode unsampled — a
  folder of real photos will exhaust memory.
- A screen owns the bitmaps it decoded: repointing an `ImageView` recycles the one it replaces, and
  `OnDestroy` detaches and frees whatever is still attached. The player screen has a **second**
  owner — the one-entry prefetch cache, holding a pose nothing has attached yet — freed on every way
  out of the screen, plus a third transient one in a decode whose result arrived too late to want.
  Its steady-state peak is therefore two full-size poses, not one, which is part of what the decode
  bound below is budgeted against. A JNI global ref keeps a Bitmap alive
  until a managed GC plus finalizer pass, which is far too late under a session's decode rate.
- A single unreadable image must never sink the screen: decode failures return `null` and are
  logged, and the session skips past them with a bounded failure budget.

## 9. Errors and logging

- All logs go through `Android.Util.Log` with the tag constant `LogTag = "FigureDrawing"`.
- Anything crossing the system boundary — SAF results, URI permission grants, launching the
  picker, image decoding, building a picker hint from a persisted tree URI — is wrapped in
  `try`/`catch`, logged, and turned into a visible message rather than a crash.
  `MainActivity.OnActivityResult` is the reference example.
- Catching broad `Exception` is acceptable at those boundaries, and only there. Elsewhere, catch the
  specific type or let it throw.
- Never log image contents or full user file paths beyond the content URI already logged.

## 10. UI resources

- Layouts are `Resources/layout/activity_*.xml`, strings are `Resources/values/strings.xml`.
- Views are resolved by `FindViewById<T>(Resource.Id.x)!` in `OnCreate` and stored in `null!` fields.
- **User-facing text always comes from `strings.xml`** via `GetString(Resource.String.y)`. No string
  literals in an Activity.
- Because these lookups are by name at runtime, a rename compiles fine and crashes on device. The
  contract tests in §11 exist to catch exactly that — a new view id or string must be added to them.

## 11. Testing strategy

Four tiers, cheapest first. Prefer the cheapest tier that can catch the bug.

**Unit tests** (`FigureDrawing.Tests`) — the default. Everything in Core is covered here, with
injected clock/`Random`/loader making them deterministic. One file per Core type, except where a
type owns several invariant families: the session aggregate has one file per family
(`DrawingSessionTests`, `DrawingSessionSetupTests`, `DrawingSessionCountdownTests`,
`DrawingSessionImageTests`, `DrawingSessionBreakTests`, `DrawingSessionTimeAccountingTests`), which
is what keeps a 350-test suite navigable after the consolidation.

**Contract tests** (`UiResourceContractTests`, `SessionScreenContractTests`, `TypefaceContractTests`,
`SourceContractTests`,
`FolderMemoryContractTests`, `AndroidBuildTests`) —
a pattern worth understanding before touching the Android layer. They parse the *source and XML as
files* rather than running them, so they need no device but still catch the runtime-only failures
that Xamarin's compile-time checks miss: a view id referenced from code but absent from the layout,
a missing string, a build property regression. `TestPaths` locates the repo root by walking up to
`FigureDrawing.sln`, since the working directory differs between `nx` and `dotnet test`.

A contract test reads *code*, not prose: `SourceContract` (shared by `FolderMemoryContractTests` and
`SessionScreenContractTests`, and tested itself in `SourceContractTests`) strips comments and string
literals before anything is asserted, because an assertion a comment can satisfy stays green
through the deletion it exists to catch — and one a comment can *break* fails a build that behaves.
It also normalises line endings, since its declaration matcher anchors on end-of-line and a CRLF
checkout would otherwise find no methods at all. It reads block bodies, not expression-bodied
members, so anything the tier needs to assert on is written with braces. Assert the APIs a method reaches and the wiring between
the screen's own methods; never a local's name, a literal's spelling, or a pattern's syntax — those
fail a refactor that still behaves. Code inside an interpolation hole is stripped with its string:
a method named in a log message is not wiring.

**E2E-model tests** (`SessionE2ETests.cs`) — drive the Core objects through a whole session in one
test, without Android: the library enumerates a fake tree, the draft produces the config, and the
session runs to its summary. A `Screen` harness inside it mirrors `SessionActivity`'s repaint loop,
so a break in that wiring fails here rather than only on a device.

**UI tests** (`FigureDrawing.UITests`) — Appium against a real emulator. Slow and last resort; use
only for behavior genuinely unreachable from Core. Run them with `scripts/run-appium-tests.ps1`,
which is the only supported entry point: it installs the toolchain, boots the emulator, builds and
installs a self-contained APK, resets app + picker state, and manages the server.

Five rules the harness depends on, each learned from a failure that looked like an app bug:

- **One session per device.** The UiAutomator2 driver installs a single instrumentation on the
  device and force-stops any running instance when a session starts, so two concurrent sessions kill
  each other and every test fails with "The instrumentation process cannot be initialized". Every
  UI-test class therefore carries `[Collection(AppiumCollection.Name)]` — one shared session, classes
  run sequentially — with `DisableTestParallelization` as the backstop.
- **Each test owns its starting state.** The suite shares one app install, so a test that picks a
  folder leaves that choice persisted for every test after it. Anything asserting first-run
  behaviour calls `UiTestEnvironment.ResetAppState()` itself rather than trusting run order.
- **Navigate by Activity, not by package.** Setup/Images/Settings are panes of `MainActivity` while
  the player is a separate Activity, so "press Back until the package is ours" is already satisfied
  on the player screen and never reaches the tabs. `AppiumGuard.ReturnToMainScreen` keys off the
  current *Activity* instead.
- **The picker's location is checked positively, never inferred.** Android refuses to grant the
  root of shared storage through `ACTION_OPEN_DOCUMENT_TREE` — it shows "Can't use this folder"
  and offers no confirm button — and that root is where DocumentsUI opens with no history, so on a
  first pick the seeded folder has to be walked into. Once a folder is remembered the app passes
  `EXTRA_INITIAL_URI` and the picker is inside it already, so the walk is skipped. `AppiumGuard.
  SelectDefaultFolder` decides between the two by asking where the picker *is* (the folder name in
  the toolbar) and fails when it is neither inside the folder nor showing it as a row — a missing
  row taken as "already inside" would confirm whatever directory happened to be on screen.
- **A UI test must be able to fail.** DocumentsUI reopens where it was last left, which mimics the
  app supplying a starting point, so `ReopeningPicker_StartsInTheLastFolder` calls
  `UiTestEnvironment.ResetPickerState()` first. The app's URI grants live in the system and
  survive it.

Rules:

- New Core type ⇒ new unit test file; a new invariant family on an existing type ⇒ its own file.
- New view id or user-facing string ⇒ add it to the contract tests.
- Logic that is hard to unit test is a design smell — that is the signal to move it into Core, not
  to write a UI test for it.

## 12. Build and run

- Run tasks through Nx, not the underlying tooling: `./nx.bat run FigureDrawing.Tests:test`.
- `Directory.Build.props` supplies the Android SDK and JDK 17 paths (Microsoft.Android 35 rejects
  JDK 25). Every rule there is guarded so it never overrides an explicit choice.
- One-command run: `dotnet build FigureDrawing.csproj -t:RunEmulator`, which delegates to
  `scripts/run-app.*`. Both the MSBuild target and a manual run go through the same script — keep it
  that way.
- The full Android compile test is opt-in via `RUN_ANDROID_BUILD_TEST=1`.

### Versioning APKs

- `version.props` is the only place a version number lives: `major.minor.patch` plus a build number
  for re-releases of the same code. Nothing else may hardcode one — the app csproj deliberately does
  not set `ApplicationVersion` / `ApplicationDisplayVersion`, and `VersionTests` fails if it starts.
- `Directory.Build.props` derives from it: `android:versionName` is `1.2.3` (or `1.2.3.4` with a
  build number) and `android:versionCode` is `major*1000000 + minor*10000 + patch*100 + build`.
  The packing is why minor/patch/build are capped at 99 — a field at 100 carries into the one above
  and two different releases would ship the same code, which a device reads as "not an upgrade".
- Bump the semantic part with `pwsh scripts/bump-version.ps1 -Patch|-Minor|-Major|-Set 2.0.0`; a
  semantic bump resets the build number to 0. CI can stamp one build without a commit via
  `-p:FdBuildNumber=N`.
- The build number belongs to `scripts/build-apk.ps1`: each run consumes the next one and writes it
  back to `version.props` after the publish succeeds, so two APKs of the same commit never share a
  versionCode. `-NoBump` builds the file as it stands; `-BuildNumber N` pins one without editing the
  file. At 99 the build stops — the reset on a semantic bump is what keeps the field in range.
- `scripts/build-apk.ps1` names its output `artifacts/FigureDrawing-<version>-<config>.apk` and
  writes a matching `.json` manifest (versionCode, commit, dirty flag, SHA-256, UTC time), so a
  generated APK can be traced back to the source it came from.

## 13. Adding a feature — the checklist

1. Write the rule as a pure type in `FigureDrawing.Core`, taking any platform need as an
   abstraction or delegate.
2. Unit test it in `FigureDrawing.Tests`.
3. Wire it into an Activity: find views, read Core state, render, forward events back.
4. Add any new view id or string to `strings.xml` / the layout **and** to the contract tests.
5. Pause and resume it correctly in the lifecycle if it involves time or callbacks.
6. Run `./nx.bat run FigureDrawing.Tests:test`.

## 14. Anti-patterns

Findings against any of these are architecture violations, not style opinions.

- A rule, calculation, or state machine implemented inside an Activity.
- `Android.*` / `Java.*` referenced from `FigureDrawing.Core`.
- `DateTime.Now`, `Stopwatch`, or `new Random()` used directly inside Core instead of the injected
  clock/`Random`.
- A `LiteDatabase` opened outside `Settings`.
- A hardcoded user-facing string in an Activity.
- Screen-to-screen data passed via a static, singleton, or `Application` field instead of intent extras.
- A `Handler` callback posted as an `Action` and expected to be removable.
- Bitmap decoding that bypasses `ImageDecoding.DecodeSampledBitmap`.
- A new view id or string added without a corresponding contract-test entry.
- A UI test written for behavior that could have been reached from Core.

---

# Part II — Domain model (DDD view)

Sections 1–14 describe the *physical* architecture: which project a type lives in and what it may
reference. Sections 15–21 describe the *domain* architecture: what the app is about, which
concepts belong together, and which rule each type is allowed to own. The per-object rules and
numbered invariants live in a companion document, [DOMAIN-MODEL.md](DOMAIN-MODEL.md). The two views must agree —
a bounded context that cannot live inside `FigureDrawing.Core` is a modelling mistake, not a
reason to put a rule in an Activity.

Derived from the code as of FD-005, following the `v3-ddd-architecture` skill. Two of that
skill's prescriptions are **deliberately not adopted**: the microkernel/plugin runtime and a
dependency-injection container. This is a single-user offline app of ~1,200 lines with no
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

Two distinctions carry real invariants and are worth stating twice:

- **Config vs Settings.** `SessionConfig` is validated, immutable, and scoped to one session.
  `Settings` is persisted, mutable, and scoped to the installation. Settings seed setup; setup
  produces config; config never writes back to settings implicitly.
- **Complete vs Skip vs End.** Three different terminal moves on a pose, with three different
  effects on `CompletedCount` and `TotalDrawingTime`. See the invariant table in §17.

## 16. Bounded contexts

Four contexts, each a cohesive vocabulary with its own rules. All four live in
`FigureDrawing.Core`; the Android layer holds only their adapters and screens.

| Context | Owns | Core types today | Namespace / folder |
|---|---|---|---|
| **Reference Library** | Discovering drawable images under a picked folder; what counts as an image; the pool; whether the remembered folder is still usable | `ReferenceLibrary`, `IDocumentTree`, `DocumentEntry`, `LibraryReference`, `PersistedGrant` | `FigureDrawing.Core` (root) |
| **Session Setup** | Parsing and validating the two inputs; the Start gate; producing a config | `SessionSetup`, `SessionConfig`, and the session's `Draft` phase | `FigureDrawing.Core` (root) |
| **Session Execution** | Running a session: sequence, passes, counts, skip semantics, time accounting, per-pose countdown, breaks, resolving an id to a displayable image, the totals, the viewing aids | `DrawingSession<TImage>`, `ViewerTools` | `FigureDrawing.Core/Session` |
| **Preferences** | The persisted settings document and its lifecycle | `Settings` | `FigureDrawing.Core/Data` |

Supporting, deliberately outside the four: **Rendering** (`ImageDecoding`, `BitmapMath`,
`GridContrast`, the `ImageView` wiring). It has no domain rules — only the memory-bound decode
policy of §8 and the legibility policy of the viewing aids. It is a shared technical service, not a
context, and `BitmapMath` and `GridContrast` are the pieces of it pure enough to live in Core.

`GridContrast` is where a viewing aid's *appearance* is decided, as against `ViewerTools`, which
owns whether that aid is on. Keeping the two apart is what lets `ViewerTools` stay a bag of pure
flags (`INV-VIEW-3`): the screen samples the decoded pose down to a small block of pixels once per
pose and asks `GridContrast` which tone each guide takes, and neither the block nor the answer is
ever session state.

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
  `MainActivity.ContentResolverDocumentTree` — that is §4's interface-adapter pattern stated as a
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
| **Domain** | `DrawingSession<TImage>`, `SessionSetup`, `ReferenceLibrary`, `ViewerTools`, value objects | `FigureDrawing.Core` |
| **Infrastructure** | `Settings` (LiteDB), `ContentResolverDocumentTree` (SAF), `ImageDecoding` (BitmapFactory) | Core `Data/` + app project |

The domain layer has no outward dependency: `FigureDrawing.Core` references only LiteDB, and only
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
   to land. On the next feature that touches it, split by context: keep view wiring in the Activity
   and move folder-loading and settings-syncing into their own adapter classes.

   *Deferred once, deliberately (remembered-folder work).* That change added `OnPause`,
   `SaveSettings`, `CaptureTypedInputs`, `RememberedTree`, `LastPickedDocumentUri`,
   `PersistedGrants`, `ReleaseSupersededGrants`, `RefreshGrant` and `ShowRememberedFolderUnavailable`
   to this class, and moved every *rule* it could into `LibraryReference` instead of splitting the
   screen. The new trigger is concrete: the next feature that adds a method here which is neither
   view wiring nor a one-line call into Core does the split first.
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
7. **Image decoding still runs on the main thread in the folder walk on launch** — up to 24 preview
   thumbnails inside `OnCreate`, specified in [FD-009](prds/FD-009-async-reference-library.md).
   (Grants are no longer accumulated: picking a different folder releases the ones it supersedes and
   a restore re-takes the one in use, `INV-REF-4`.) Not a Core concern, and not new.

   Three entries that used to sit here are closed: the pool no longer crosses to the player whole
   (it is sampled to a handoff bounded by the session's own length, `INV-POOL-6`), decoded bitmaps
   are recycled by the screen that decoded them, and the player screen no longer decodes on the UI
   thread at all — the next pose is decoded during the current one, the session's construction (and
   with it the first pose) happens off the thread too, and an unreadable file is loaded once per
   session rather than once per pass (`INV-PLY-7`, `INV-PLY-8`, FD-010).

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
  Android implementations are covered by the contract tests only to the extent that their view ids
  and strings exist.
- **Nothing about the domain is tested through Appium.** A domain rule reachable only from a UI test
  is a rule in the wrong layer (§14).

Success criteria for the DDD structure, checkable rather than aspirational:

- [x] `FigureDrawing.Core` has zero `Android.*` / `Java.*` references — guarded by project setup and `AndroidBuildTests`
- [x] Each Core type belongs to exactly one context in the §16 table; new types are added to it
- [x] The catalogue stays at ten objects unless a new one earns its place — a new Core type must
      justify itself against [DOMAIN-MODEL.md §9](DOMAIN-MODEL.md#9-consolidation), or be added to
      the catalogue with its own invariants and tests
- [ ] Context dependencies stay acyclic and match the §16 map — Execution never reads settings, Setup never reads the pool's contents
- [ ] Every invariant in §17 has a named unit test
- [x] No aggregate mutates through a public field or a setter — commands only
- [ ] No rule reachable only through an Activity (the §14 list, plus the §20 findings closed)
- [x] Any new time or randomness in Core arrives through an injected `Func<TimeSpan>` / `Random`
