# Architecture — FigureDrawing

How this codebase is organized and the rules changes must hold to. Written for agents and
contributors reviewing or extending the app. Product scope lives in the root [README](../README.md);
planned work lives in [GitHub Issues](https://github.com/JeffreyMcKinley/FigureDrawing/issues).

> Derived from the code as of #4. Where a rule below is marked **(confirm)** it was inferred
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
| `LoadGeneration` | Which piece of background work may still publish its result (`INV-X-13`) |
| `LibraryLoadState` | Whether a re-walk may keep the pool the screen is already showing |
| `Data/Settings` | The persisted settings document and its LiteDB store |

Ten catalogued objects, nine of them Core types today (`SessionRecord` is still proposed),
deliberately: what each one is and why the neighbours it absorbed are not
separate concepts is [DOMAIN-MODEL.md §1](DOMAIN-MODEL.md) and [§9](DOMAIN-MODEL.md#9-consolidation).

`BitmapMath`, `GridContrast`, `LoadGeneration` and `LibraryLoadState` are **not** among those ten and
are not in the catalogue ([DOMAIN-MODEL.md §1](DOMAIN-MODEL.md)): they hold no domain rule and name
nothing in the ubiquitous language. They are supporting types (§16) that live here because Core is
where a thing becomes testable. Note `LoadGeneration` is deterministic and Android-free but *not*
stateless — it is the app's one piece of shared cross-thread state. That is the admission criterion:
Android-free and executable by a test, not "stateless".

Core types are deterministic and side-effect free apart from `Settings`. They expose state as
properties for the screen to read (`CurrentImage`, `Remaining`, `IsComplete`, `Display`) and accept
commands as methods (`Next`, `Skip`, `End`, `Tick`, `Pause`, `Resume`). Starting a session is the
running constructor, not a command — there is no public `Start` and no public `Restart`; restarting
the clock is internal (`RestartCountdown`).

### Android — screens and their adapters

| Type | Owns |
|---|---|
| `MainActivity` | The three tabbed panes: setup inputs, the reference library + folder picker (SAF), settings |
| `SessionActivity` | The player screen: pose, rail, break/pause overlays, summary, the repaint loop, lifecycle |
| `ImageDecoding` | Two-pass `BitmapFactory` decode shared by both screens |
| `LibraryLoader` / `LibraryLoad` | Not a screen: reads a reference library off the UI thread — the SAF walk, the preview decodes, the SAF adapter, and the guard that discards a superseded load (`INV-X-13`) |

The look is the **Nocturne** design system, imported from the Claude Design project *Figure Drawing
Practice App*. Its tokens live in `Resources/values/colors.xml` + `dimens.xml`, and its component
classes (`.tool-chip`, `.btn-*`, `.card`, `.input`, the tab bar) are the widget styles in
`Resources/values/styles.xml`. Retune the system there rather than styling a control inline; a
one-off `android:background` on a button is the same kind of violation as a rule in an Activity.

Two Claude Design projects sit behind that: **Nocturne** is the theme
(the ramps, the scale, the component classes), while the **design** — *Figure Drawing Practice App*,
`Figure Drawing App.dc.html` — is the five screens themselves. Several of the app's numbers exist
only in the design and not in the theme at all: the pose stage's `#0f1119`, the break and pause
overlay alphas, the 16% chip wash, the rail.

`docs/design/` therefore mirrors both, plus `tokens.json` — the reviewed bridge from their values to
the Android resource names above, recording which upstream each number came from and why the app
diverges where it does. That makes the *repo* the source of truth and lets multiple people
collaborate on the look through git.

The bridge is documentation, not a build input: nothing in the app reads it, and no test asserts it.
Design sync is a workflow around this repo rather than part of the product, so it is reviewed by
reading the diff. Read [docs/design/README.md](design/README.md) before changing a token on any
side.

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
`LibraryLoader.ContentResolverDocumentTree` backs it with `DocumentsContract` + `ContentResolver`;
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
  changes (for folder picks, only after a successful load) and again in `OnPause` (which first
  captures the typed inputs, the only values living nowhere else), since a swipe off the recents
  list never reaches `OnDestroy`. `Settings.Save`
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

- Core is synchronous by design and never threads, sleeps or posts (`INV-X-9`). Two screens do
  background work, and nothing else does. The player decodes reference images off the UI thread —
  the session's own construction (`BuildSession`, which resolves the first pose) and the next pose
  while the current one is up (`PrefetchUpcoming` / `DecodeAhead`). The setup screen reads a
  reference library off it (`LibraryLoader`): the folder walk and up to 24 preview decodes.
  Everything else runs on the main thread.
- **One decode at a time per screen.** The prefetch holds a single slot (`prefetchTask`), and a
  request while it is occupied is dropped rather than queued — the slot re-aims itself when the
  decode settles. Without that bound, every command that changes which image is next starts another
  decode, and a few quick taps put several full-size decodes in flight at once. The library load has
  the same property by a different route: one load runs at a time and a newer one supersedes it.
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

**The shape for background work.** `SessionActivity.PrefetchUpcoming` / `DecodeAhead` /
`CancelPrefetch` and `LibraryLoader` are the two worked examples; a third piece of background work
follows this or argues why not.

- One `async Task` method in the Android layer wrapping **one** `Task.Run`. Never `async void`: an
  exception after the first await in an `async void` is rethrown on the looper and kills the process,
  which §9 forbids at a boundary.
- The worker `Task.Run` calls is a **`static`** method taking everything it needs as parameters, and
  the delegate handed to `Task.Run` may capture only the owning helper — never an Activity. That is
  what makes "the background thread cannot touch a view or `Settings`" structural rather than a
  review comment. The subtle half is the `ContentResolver`: take it from `ApplicationContext`, since
  an Activity's resolver reaches back to the Activity through its `ContextImpl` and would pin a
  destroyed screen's whole view tree for the length of the work.
- Results marshal back on the main-looper `SynchronizationContext`, which is what `await` resumes on
  — the same main *looper* the repaint loop posts through, though not the same `Handler`. Do not add
  a redundant `RunOnUiThread` inside a method that already resumes there.
- **Never block on it.** `.Result` / `.Wait()` against the main-looper context deadlocks, and would
  restore the freeze the work exists to remove.
- Work in flight is **abandonable**: an `int` generation taken on the way out and re-checked on the
  way back in, so only the most recent result may be written (`INV-X-13`). A `CancellationTokenSource`
  buys nothing here — nothing on the path is a cancellation-aware API, so every check is an ordinary
  `if` — and costs a disposal puzzle. Anything the abandoned work allocated is freed on that branch:
  cancelling frees nothing by itself, it only says which branch to take.
- Abandon in `OnStop` and `OnDestroy`, and wherever the screen deliberately drops the work it is
  showing (`MainActivity.ResetLibrary`). `OnStop` matters as much as `OnDestroy`: a result landing
  after the screen released its resources would put them straight back.
- **Where the boundary sits is per-feature, and must be argued.** The library load abandons at
  `OnStop`, never `OnPause`, because a briefly backgrounded app (a notification shade, a permission
  dialog) must not come back to an empty library. Work whose result is worthless the moment the
  screen stops being visible — a pose prefetch, say — may abandon earlier. State the reason wherever
  the choice is made; do not copy this one by default.
- Everything after the await catches its own failures — including the recovery path, since the task
  is discarded by its caller and an escape there would fault unobserved, leaving the screen mid-load
  with nothing logged. By then no caller is left to catch anything: the try/catch around the call
  site only ever covered the synchronous prologue.
- **Abandoning is a publish gate, not an interrupt.** It decides whether a result may be written; it
  cannot unblock a call already in flight, so a provider that never answers parks a pool thread
  until it does. The screen stays responsive, which is the property that matters — but do not read
  the abandonment rule as a guarantee that the work has stopped.
- **The stored-runnable rule above is about the repeating repaint queue, not about every callback.**
  A one-shot background result comes back through the `await` continuation, which Android's
  synchronization context posts to the same main looper. That continuation is deliberately *not*
  removable, and must not be made so: it is the code that frees a bitmap nobody wants any more, so a
  `RemoveCallbacks` that succeeded would strand the pixels with nothing left to release them. What
  makes it safe instead is the generation counter compared after the `await`.
- **Never `ConfigureAwait(false)` in an Activity.** The continuation touches views and the fields
  that own bitmaps; resuming it on a pool thread makes every one of those a race. It is also the
  memory barrier that publishes decoded pixels to the thread that attaches them.
- A decode already running cannot be stopped, so "cancel" means "abandon the result", not "abort the
  work". A `CancellationToken` still earns its place where the work has not started:
  `ImageDecoding.DecodeSampledBitmap` checks one between its bounds pass and its decode pass, which
  is where the multi-megabyte allocation begins. The player passes one; the library load bounds the
  same cost by checking its generation per cursor row and per image instead. Abandoning must not
  clear the prefetch slot — the work is still running, and a free slot would let the next repaint
  start a second decode beside it.
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
- **The reference grid's previews are released in `OnStop` and rebuilt in `OnStart`**, not held to
  `OnDestroy`. `MainActivity` is *stopped*, not destroyed, while `SessionActivity` runs, so previews
  held any longer would sit under every session and the app's real peak would be the grid plus the
  pose plus the pose being decoded. This is affordable only because the rebuild is off the UI thread
  (§7), and it depends on `SessionActivity` being opaque and full-screen — a translucent or dialog
  theme would stop `MainActivity` ever reaching Stopped, and the release would silently stop running.
  The rebuild re-walks the folder, which is also what picks up images added in another app
  (`INV-GRP-1`). One exception, because the folder picker also stops this screen: while a pick is in
  flight the rebuild is deferred to its result, which knows the new folder — otherwise every pick
  would walk the folder it is about to replace.
- A screen owns the bitmaps it decoded — or, where a helper decoded them for it, that helper owns
  them until they are attached: repointing an `ImageView` recycles the one it replaces, and
  `OnDestroy` detaches and frees whatever is still attached. A decoded preview is therefore always
  owned by exactly one of the two, never both and never neither: `LibraryLoader` frees anything the
  grid did not take, including everything an abandoned load decoded. The player screen has a
  **second** owner of its own — the one-entry prefetch cache, holding a pose nothing has attached
  yet — freed on every way out of the screen, plus a third transient one in a decode whose result
  arrived too late to want. Its steady-state peak is therefore two full-size poses, not one, which
  is part of what the decode bound below is budgeted against. A JNI global ref keeps a Bitmap alive
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
is what keeps a suite of this size navigable after the consolidation.

**Contract tests** (`UiResourceContractTests`, `SessionScreenContractTests`, `TypefaceContractTests`,
`SourceContractTests`, `CrossActivityContractTests`, `FolderMemoryContractTests`,
`LibraryLoadContractTests`, `AndroidBuildTests`) —
a pattern worth understanding before touching the Android layer. They parse the *source and XML as
files* rather than running them, so they need no device but still catch the runtime-only failures
that Xamarin's compile-time checks miss: a view id referenced from code but absent from the layout,
a missing string, a build property regression. `TestPaths` locates the repo root by walking up to
`FigureDrawing.sln`, since the working directory differs between `nx` and `dotnet test`.

A contract test reads *code*, not prose: `SourceContract` (shared by `FolderMemoryContractTests`,
`SessionScreenContractTests`, `CrossActivityContractTests` and `LibraryLoadContractTests`,
and tested itself in
`SourceContractTests`) strips comments and string
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

Moved to [DDD-ARCHITECTURE.md](DDD-ARCHITECTURE.md) to keep physical architecture (§1–14) and
domain architecture (§15–21) separately loadable. Sections 15–21 live there with all content
preserved.
