# Domain Model — FigureDrawing

The objects this app is made of, and the rules each one must obey. One card per object: what
identifies it, how long it lives, what it guarantees, what it is allowed to do, and what it must
never do.

Companion to [ARCHITECTURE.md](ARCHITECTURE.md) — that document says *where* code lives and which
project may reference which; this one says *what the objects mean*. Vocabulary is defined once in
[DDD-ARCHITECTURE.md §15](DDD-ARCHITECTURE.md#15-ubiquitous-language) and used here without redefinition.
Bounded contexts are [§16](DDD-ARCHITECTURE.md#16-bounded-contexts).

## How to read this

Every object carries a **status**, because this model covers both what exists and what the MVP
still needs:

| Status | Means |
|---|---|
| **Implemented** | A Core type exists and enforces the rules below |
| **Implicit** | The rules are enforced, but by a primitive or by logic spread across types — no named type owns them |
| **Proposed** | Not built. The rules are the design contract for the ticket that builds it |

Invariants are numbered (`INV-<family>-<n>`) so a ticket, test name, or review comment can cite
one. An invariant is a rule that must hold at every point an outside caller can observe the
object — not merely on entry and exit of one method.

**Invariant ids are stable across the consolidation.** The catalogue below merges objects that were
once separate cards, but a merged object keeps every id family it absorbed rather than renumbering,
so an existing citation in a ticket, test name, or review comment still resolves. One object owning
several families (`DrawingSession` owns five) is therefore expected, not a modelling smell. Two
families read alike and are not: `INV-SET-1..5` is the setup gate, `INV-SET-P*` is preferences.

Object kinds use the standard meanings: **Entity** has identity and changes over time; **Value
object** is immutable and compared by its contents; **Aggregate root** is the only legal entry
point to a cluster of objects; **Domain service** is stateless behaviour that belongs to no single
object; **Port** is an interface the domain defines and the platform implements.

---

## 1. Object catalogue

Ten objects, down from fifteen (the tenth arrived after the merges, not before them). The count is the point: this is a single-user offline app, and a
concept that never varies independently of its neighbour does not earn its own type.

| # | Object | Kind | Context | Invariant families | Status |
|---|---|---|---|---|---|
| 1 | `Pose` | Value object | Reference Library → Session Execution | `INV-IMG-*`, `INV-POSE-*` | Implicit (`string` id + session state) |
| 2 | `ReferenceLibrary` | Aggregate root | Reference Library | `INV-GRP-*`, `INV-POOL-*` | Implemented |
| 3 | `IDocumentTree` / `DocumentEntry` | Port / value object | Reference Library | `INV-TREE-*` | Implemented |
| 4 | `LibraryReference` / `PersistedGrant` | Domain service / value object | Reference Library | `INV-REF-*` | Implemented |
| 5 | `SessionSetup` | Domain service | Session Setup | `INV-SET-1..5` | Implemented |
| 6 | `SessionConfig` | Value object | Session Setup → Execution | `INV-CFG-*` | Implemented |
| 7 | `DrawingSession<TImage>` | Aggregate root | Session Execution | `INV-SES-*`, `INV-CD-*`, `INV-PLY-*`, `INV-SUM-*`, `INV-POSE-*` | Implemented |
| 8 | `ViewerTools` | Entity (no identity) | Session Execution | `INV-VIEW-*` | Implemented |
| 9 | `Settings` | Aggregate root | Preferences | `INV-SET-P*`, `INV-STO-*` | Implemented |
| 10 | `SessionRecord` | Entity | History | — | Proposed |

What was merged, and why, is [§9](#9-consolidation). Read a card first; the mapping is only needed
when touching the code.

---

## 2. Reference Library objects

### 2.1 `Pose` — *Implicit*

One picture the artist draws from, shown once for the configured duration. Image and display are
one object: a reference image the app never shows is not a domain concept, and a pose without an
image cannot exist. Identity is the image id; the pose adds when it started, how it ended, and
nothing else.

| Aspect | Rule |
|---|---|
| **Identity** | The SAF content URI string. Two poses show the same image if and only if the strings are equal — no normalization, no case folding, no path comparison |
| **Kind** | Value object. Immutable, compared by value |
| **Lifetime** | The id outlives the app; the pose lasts from the moment the image goes on screen until it is completed, skipped, or cut short |
| **State** | The id, and nothing else. Dimensions, format, and orientation are decode-time facts; remaining time and outcome are the session's state, not the pose's |

**Rules**

- `INV-IMG-1` — **The id is opaque.** No domain code parses it, splits it, sorts by it, infers a
  file name or extension from it, or logs more of it than an id. Only the Android adapter, which
  produced it, may interpret it. This is what lets tests use `"a"`, `"b"`, `"c"`.
- `INV-IMG-2` — **Existence is not guaranteed.** An id may become unreadable at any moment
  (permission revoked, file deleted, provider offline, decode failure). Every consumer must treat
  "cannot read this image" as ordinary, not exceptional.
- `INV-IMG-3` — **Format is decided by MIME, never by extension.** Anything whose MIME type starts
  with `image/` is a reference image. No hardcoded extension list. A directory is never an image.
- `INV-IMG-4` — **Never cached as decoded pixels beyond the current screen.** A decoded image is a
  rendering artifact, not a domain object: its LONG side is bounded to within 2x `MaxImageDimension`
  (1080 px) whatever the aspect ratio is, and the screen that decoded it frees it.
- `INV-POSE-1` — **A pose ends exactly once**, in exactly one of three ways: completed (timer
  expiry or manual done-tap), skipped, or cut short by ending the session.
- `INV-POSE-2` — **A new pose gets the full configured duration.** No carry-over from the previous
  pose, ever.
- `INV-POSE-3` — **The pose clock restarts whenever the current image changes.** Advancing the
  image and restarting the clock are one operation, owned by `DrawingSession` — never a pairing a
  screen is trusted to repeat.
- `INV-POSE-4` — **Duration is uniform within a session.** Per-pose durations (long-pose warmups,
  ramping timers) would make `SessionConfig` carry a schedule instead of a scalar — a model change,
  not a parameter tweak.

**May not** — carry a caption, rating, tag, or "seen" flag. If per-image state is ever needed, it
becomes an entity with its own store, and that is a deliberate model change.

### 2.2 `ReferenceLibrary` — *Implemented*

Everything between "the user picked a folder" and "here is the ordered list a session may draw
from": the picked tree, the traversal that discovers images beneath it, and the resulting pool.
One object, because the pool has no meaning apart from the tree that produced it and the traversal
has no state to keep between them.

| Aspect | Rule |
|---|---|
| **Identity** | `RootDocumentId` — the root document id of the picked tree. Stable across launches; the tree URI it came from is persisted separately, by the Android layer, in `Settings.LastCollection` |
| **Kind** | Aggregate root over its images |
| **Lifetime** | Persisted by reference (`Settings.LastCollection`), never by contents |
| **State** | Root document id, display name, and the enumerated image ids in encounter order |
| **Operations** | the constructor (which *is* the walk — re-deriving membership means a new instance, never a second walk on a live one, `INV-X-13`), `Pool` (the ordered ids), `Sample(maxIds[, maxTotalIdLength], random)` (the bounded handoff, `INV-POOL-6`), `Count` / `IsEmpty`, `RootDocumentId` / `DisplayName`, the static `Empty` (no folder picked yet — every screen's starting state, a fresh instance per
access rather than process-wide state two screens could come to share, `INV-X-3`) and the static
classifiers `IsImage` / `IsDirectory` |

**Mapping ids at the edge.** The constructor takes an optional `toImageId` so the adapter can turn
each document id into the durable content URI a session draws from — the Android layer passes
`DocumentsContract.BuildDocumentUriUsingTree`, tests pass nothing and keep using `"a"`, `"b"`,
`"c"`. That is what makes `Pool` *the* session pool rather than a list the screen has to re-map.
A document id the mapper rejects (returns `null`) or cannot map (throws, which the provider can
cause by reporting a row from outside the tree) is dropped from the pool. One bad row never aborts
the walk (`INV-X-11`).

**No `IsAvailable` on the aggregate.** The library is its contents, so "may I still read this
folder" is a question about the *reference*, not about the pool. It is answered by
`LibraryReference.HasReadGrant` (§2.4), which the screen feeds the platform's grant list — and that
is why a revoked grant is no longer indistinguishable from an empty folder: it has its own message
(`INV-GRP-4`, `INV-GRP-5`, `INV-REF-3`).

**Rules — the group**

- `INV-GRP-1` — **Membership is derived, never stored.** The images are whatever the document tree
  reports *now*. The app never persists a list of image ids — a persisted list would silently rot
  as the user edits the folder. Re-walk on load, by building a fresh library.
- `INV-GRP-2` — **The library is flat.** Subfolders are traversed depth-first and their images
  merge into one pool in encounter order. Nesting is a storage detail, not a domain hierarchy. If
  per-subfolder grouping is ever wanted, that is *several* libraries, decided at pick time.
- `INV-GRP-3` — **Enumeration terminates.** A provider that reports a document as its own
  descendant must not loop; visited document ids are tracked. The visited set alone is not enough:
  a provider that synthesizes a fresh id at every level never repeats one, so the walk is bounded
  by depth as well (64 below the root — deeper than any real photo library). The two guards answer
  different attacks and neither subsumes the other. Depth matters more than a cycle would: the walk
  recurses, so an unbounded one is a stack overflow, which kills the process rather than raising
  something `INV-X-11` could catch. Stopping early yields a partial pool, which is legal for the
  same reason an abandoned load's is (`INV-GRP-1`, `INV-X-13`).
- `INV-GRP-4` — **It may be empty, and empty is not an error.** Zero images shows the empty state,
  blocks Start, and does not crash.
- `INV-GRP-5` — **Access can expire, the choice does not.** The library is only usable while its
  persisted read permission is still held, and a revoked grant is an expected outcome: fall back,
  log, and let the user pick again. Never crash and never prompt in a loop. The reference itself
  is *not* forgotten when access is — a remembered folder that cannot be reopened says so and
  still aims the picker (`folder_unavailable_text`), because showing the first-run state instead
  reads as the app having lost the choice.
- `INV-GRP-6` — **Order is enumeration order.** The library's own order is deterministic and
  provider-driven. Randomizing the *order* belongs to the session, never here — the one random
  choice the library makes is *which* ids cross a bounded handoff (`INV-POOL-6`), and even that
  returns them in enumeration order.

**Rules — the pool it hands over**

- `INV-POOL-1` — **Ordered and stable.** Index order is fixed for the session's lifetime.
- `INV-POOL-2` — **No duplicates.** The same id appears at most once. Repetition within a session
  comes from passes (`INV-SES-4`), never from a pool that lists an image twice.
- `INV-POOL-3` — **May be smaller than the session's target count.** That is normal and is exactly
  what passes exist to handle.
- `INV-POOL-4` — **Copied, not aliased.** A session copies the pool at construction, so a later
  re-walk cannot change a running session.
- `INV-POOL-5` — **A pool with zero images cannot start a session.** Enforced upstream by the Start
  gate (`INV-SET-3`) and defensively downstream (`INV-SES-7`).
- `INV-POOL-6` — **A handoff may be bounded, and says so.** When the pool cannot cross a boundary
  whole (the player receives it as an intent extra, which has a hard size limit in bytes), the
  library hands over a uniform random sample in enumeration order, bounded by both a count and a
  total id length (`ReferenceLibrary.Sample`). A length bound is not a count bound in disguise — a
  document id carries the whole relative path, so a deep tree with long filenames runs several
  hundred characters per id where a shallow one runs fifty. Either bound at zero or below yields an
  empty pool; past that the first id is taken whenever the count allows it, even when it alone
  exceeds the length bound, so one pathological id yields a one-image pool rather than an empty one
  that cannot start a session (`INV-POOL-5`). The count bound is the uniform one: where the length
  bound binds it trims the tail of the already-ordered sample, so the ids that survive that trim
  skew earlier in enumeration order. The count is the one place the library chooses at random,
  and it chooses membership, never order (`INV-GRP-6`). The library itself is never truncated —
  membership is still whatever the tree reports (`INV-GRP-1`, `INV-GRP-2`) — and the session draws
  from the sample as if it were the pool, so `INV-POOL-1`..`INV-POOL-4` hold unchanged on what it
  received.

  How wide that bound is comes from the session's own length, not from the transport alone
  (`SessionSetup.HandoffBound` — a setup rule, so the library is not made to know how long a session
  is): the player is handed an array and cannot ask the library for more, so the sample is also what
  "run it again" redraws from. It is a multiple of the pose count, floored so a short session still
  draws from a spread and ceilinged by what the transport carries, the ceiling winning if the two
  ever disagree. The trade against a flat bound is real and deliberate: a mid-sized library now
  crosses in part rather than whole, so a repeated run redraws from the sample, not the folder.

**Traversal is stateless.** Classification (`IsImage`, `IsDirectory`) and the depth-first policy
hold no state between calls, so two callers can never interfere — that property must survive the
merge into the aggregate.

**May not** — copy, move, rename, or delete anything the user owns. The app is strictly read-only
against user storage.

**Multi-library, when it lands** *(Proposed)* — a session draws from a selection of one or more
libraries. Rules that must hold then: the pool is the concatenation of each library's images in
selection order; duplicate ids across libraries collapse to one entry (`INV-POOL-2`); removing one
from the selection never disturbs a running session, because the pool was copied at construction
(`INV-POOL-4`).

### 2.3 `IDocumentTree` / `DocumentEntry` — *Implemented*

The port through which the domain sees storage, and the one row type it understands. Kept separate
from `ReferenceLibrary` on purpose: it is the anti-corruption layer, and an ACL that lives inside
the aggregate it protects is not an ACL.

**Rules**

- `INV-TREE-1` — **The port is the only door.** `DocumentsContract`, `ContentResolver`, `Cursor`,
  and `Uri` stop at the adapter. Nothing SAF-shaped crosses into the domain, with one stated
  carve-out: the *form* of the reference the app itself persisted is domain knowledge, and
  `LibraryReference` (§2.4) recognises a `content://` tree URI without ever constructing, resolving
  or querying one.
- `INV-TREE-2` — **`GetChildren` returns direct children only.** Recursion is the library's job,
  not the adapter's, so the adapter carries no domain logic — only the query and, since #4, the
  abandonment check that stops it answering once its load is superseded (`INV-X-13`, pinned by
  `LibraryLoadContractTests`).
- `INV-TREE-3` — **A `DocumentEntry` is `(DocumentId, MimeType?)` and nothing more.** A null or
  unknown MIME type is legal and simply is not an image.
- `INV-TREE-4` — **The adapter never throws through the port.** A failed query yields nothing.

---

### 2.4 `LibraryReference` / `PersistedGrant` — *Implemented*

The *remembered* library: the string `Settings.LastCollection` carries between launches, and the
rules for deciding what it is still worth doing with. `PersistedGrant` is one entry of the
platform's permission list — a reference, and whether it is a read grant — reduced to what those
rules need.

Not folded into `ReferenceLibrary`, because the aggregate is a folder the app can read *now* and
every question here is asked when there is no library yet: on launch before anything is loaded, and
on a picker tap that may be about to replace it. Not folded into `Settings`, because the store's job
is to hold the string, not to know what makes one usable (§5.1).

| Aspect | Value |
|---|---|
| Kind | Domain service (stateless) + value object |
| Context | Reference Library |
| Identity | None — the reference names the folder, and is its identity |
| Lifetime | None; every operation is a pure function of its arguments |
| Operations | `TryParse`, `HasReadGrant`, `GrantsToRelease`, `Classify` |

**Rules**

- `INV-REF-1` — **A reference has a form.** Non-blank, within `MaxLength`, a `content://` scheme, and
  a `/tree/` segment carrying a non-empty document id under a real authority. Anything else is "no
  folder remembered", never an error. The length ceiling is not a guess at how long a folder name
  gets — one authority plus one document id is already far inside it. It is there because this value
  is read back from a file this app does not fully control and then handed to the system picker in a
  Binder transaction, which is the same pressure that bounds the pool handoff (`INV-POOL-6`).
- `INV-REF-2` — **One spelling.** Comparison is over a canonical form: lower-case scheme, upper-case
  percent-escapes, everything else byte-exact. The stored value and the platform's grant list are
  two round-trips through the same folder and do not always come back spelled identically; the
  document id itself stays opaque (`INV-IMG-1`).
- `INV-REF-3` — **A grant is read, held, and ours.** Only a read grant for the same reference counts.
  A write-only entry is someone else's, and a missing one means remembered-but-unreachable rather
  than never-picked (`INV-GRP-5`).
- `INV-REF-4` — **Superseded grants are handed back.** Picking a different folder releases the read
  grants held for folders that are no longer remembered; re-picking the same one releases nothing.
  A package's persisted grants are capped and the platform drops the *oldest*, so a grant kept for a
  folder the artist has moved on from can cost them the one they still use. A grant is handed back
  in the platform's own spelling, not in the canonical form `INV-REF-2` compares over: canonicalising
  is how two round-trips are recognised as one folder, but the string that *identifies* a grant to
  the platform is the one the platform reported.
- `INV-REF-5` — **Four states, four things to say.** `NeverPicked`, `Unavailable`, `Empty`, `Ready`.
  The screen maps them to messages; it does not decide them. Collapsing `Unavailable` into
  `NeverPicked` is what makes a revoked permission read as "the app forgot my folder". A listed
  grant is not on its own enough to be readable: a walk that was attempted and threw leaves the
  reference `Unavailable` too, because what the artist cannot open is unavailable whatever the
  permission list still says.

---

## 3. Session Setup objects

### 3.1 `SessionSetup` — *Implemented*

Stateless domain service: parses the setup inputs, answers whether Start is allowed, and produces
the config. The evaluated result it returns is a draft session (`DrawingSession` in its `Draft`
phase, §4.1) rather than a separate state object.

It also owns the two things derived purely from a configured session's shape: `EstimateSeconds` (how
long the run takes) and `HandoffBound(imageCount, maxIds)` (how many reference ids that run needs in
play, `INV-POOL-6`). The second lives here rather than on `ReferenceLibrary` because the library
supplies the pool and must not have to know how long a session is — the dependency runs one way
(DDD-ARCHITECTURE.md §16).

**An estimate counts the gaps, not the poses.** `EstimateSeconds` is every pose plus one break
between each *adjacent pair* — a session of n poses has n-1 breaks, because a break after the last
pose is time the artist has already stopped drawing for.

**`HandoffBound` is total for every `int` it can be handed.** `INV-SET-1` validates the count as
"> 0" and nothing more, so a pasted nine-digit number reaches the multiplication that sizes the
handoff. It is computed in 64-bit and then narrowed, because an overflow there wraps negative and a
negative bound is an empty pool (`INV-POOL-5` then refuses to start the session) — a wrong answer
that looks like an empty folder.

**The multiple is deliberately greater than one.** The player is handed an array and cannot ask for
more, so "run it again" redraws from the same ids; at exactly the pose count a second run would
replay the first run's images in a new order rather than showing different ones.

**The presets are the model's, not the screen's.** `SecondsPresets` and `BreakPresets` are the
quick-pick values *and* their render order. The setup screen walks them to build its chip rows and
never hardcodes a value it binds, so adding a preset is a change here and nowhere else.

**Rules**

- `INV-SET-1` — **Parsing is domain logic, not UI logic.** Blank, non-numeric, and non-positive
  input all evaluate to "absent". Surrounding whitespace is tolerated so a stray space cannot
  disable Start.
- `INV-SET-2` — **Both inputs must be strictly positive.** Zero is not a session. The break is the
  exception: zero is a legal break and means "no break".
- `INV-SET-3` — **Start requires all three:** a library with at least one image, a valid seconds
  value, and a valid count. The screen binds the button's enabled state to this and decides nothing
  itself.
- `INV-SET-4` — **Evaluation is pure and total.** Same inputs, same result; no input string throws.
  It is called on every keystroke, so it must stay cheap — a draft session allocates its own fields
  and nothing else: no pool copy, no clock started, no traversal.
- `INV-SET-5` — **A config exists only when the setup is startable.** No partially valid config can
  be obtained.

### 3.2 `SessionConfig` — *Implemented*

`(SecondsPerImage, ImageCount, BreakSeconds)` — the validated contract handed to a session.

**Rules**

- `INV-CFG-1` — **Immutable.** A config never changes mid-session. Changing a setting starts a new
  session.
- `INV-CFG-2` — **Validated at the boundary, trusted after it.** Session Execution does not
  re-validate; it clamps defensively (`INV-SES-7`) rather than rejecting.
- `INV-CFG-3` — **It is the whole contract.** A new per-session input is a new field here and at
  both ends of the intent-extra boundary — never an out-of-band static or a settings read from
  inside the session.
- `INV-CFG-4` — **Distinct from `Settings`.** Config is per-session and immutable; settings are
  per-installation and mutable. Neither is derived from the other automatically: the setup screen
  copies values across explicitly, in both directions, at named moments (seed on launch, persist on
  Start).

---

## 4. Session Execution objects

### 4.1 `DrawingSession<TImage>` — *Implemented*

**Aggregate root.** One run of N poses under one config, from the moment the setup inputs are first
evaluated to the summary the artist reads at the end. It owns the sequence, the counts, the pose
clock, the break, the resolution of an image id into something displayable, and the totals. There
is exactly one live session per player screen, and it is the only object that screen commands.

The reason it is one object and not five: every one of those pieces changes only when the session
advances, and each of the old separate types existed to serve exactly one other — a countdown with
no session to time, a player with no session to advance, and a summary that is a projection of
fields the session already holds, are not independent concepts. They were six types
(`SessionSetupState`, `DrawingSession`, `SessionPlayer<TImage>`, `PoseCountdown`,
`PoseSession<TImage>`, `SessionSummary`); see [§9](#9-consolidation).

| Aspect | Rule |
|---|---|
| **Identity** | None. A session is positional — one live session per player screen — so it is never stored, compared, or resumed. Giving it an id is a deliberate model change (see §6) |
| **Lifetime** | A draft is evaluated on the setup screen; constructing one with a pool copies it and positions the session on its first displayable image; it ends at completion or `End()`. Never restarted — construct a new one |
| **Phases** | `Draft` → `Pose` ⇄ `Break` → `Complete` |
| **State** | Parsed setup inputs; the upcoming queue for the current pass; current image id and its loaded image; completed and skipped counts; accumulated drawing time; time left on the current phase; run/pause state |
| **Commands** | `Next()`, `Skip()`, `End()`, `Tick() -> SessionTick`, `Pause(PauseReason = Lifecycle)`, `Resume()` |
| **Queries** | Every phase: `Phase`, `SecondsPerImage`, `ImageCount`, `BreakSeconds`, `FolderSelected`, `Config`. Draft: `SecondsValid`, `CountValid`, `CanStart`, `EstimateSeconds`. Running: `CurrentImage`, `CurrentImageId`, `UpcomingImageId` (the peek, `INV-PLY-7`), `Display`, `TimeRemaining`, `SecondsRemaining`, `IsExpired`, `PhaseDuration`, `RemainingPercent`, `OnBreak`, `IsPaused`, `PausedByUser`, `IsRunning`, `CompletedCount`, `SkippedCount`, `TargetCount`, `Remaining`, `CurrentPoseNumber`, `IsComplete`, `CouldNotDisplayImage`, `ImagesDisplayed`, `TotalDrawingTime`, `AveragePoseTime` |
| **Statics** | `Evaluate(...)` (the draft factory) and, on the non-generic partner type, `DrawingSession.Format(seconds)` |

`Remaining` counts *images* left, `TimeRemaining` counts the current phase's *time* — two different
scarcities, deliberately not sharing a name.

**One object, two read surfaces.** A draft answers the clock queries with a stopped, zero-length
phase, and a running session keeps answering `Config` with the config it runs under. Only `CanStart`
is gated on the phase: a session that has already started, or finished, cannot start (`INV-CFG-1`).
Reading a run query off a draft is legal and meaningless — the phase says which half is live.

**Rules — the run**

- `INV-SES-1` — **Commands only.** All mutation goes through the commands above. No public setter,
  no writable field, no way to nudge the count or the clock from outside.
- `INV-SES-2` — **Counting is exact.** `CompletedCount` never exceeds `TargetCount`; `Remaining` is
  never negative.
- `INV-SES-3` — **Only `Next` counts.** `Skip` and `End` never increment the completed count. `End`
  does not count the pose in progress, because it was not finished.
- `INV-SES-4` — **Every image is shown once before any repeat.** Each pass is a fresh traversal of
  the whole pool; a new pass is built only once the previous one is drained. Shuffling, when
  enabled, is Fisher-Yates over the injected `Random`, so a seeded test is reproducible.
- `INV-SES-5` — **Drawing time excludes skipped time.** Time is banked in `Next` (a completed pose)
  and in `End` (the final partial pose). `Skip` banks nothing, so the skipped seconds are gone from
  the total.
- `INV-SES-6` — **After completion, every command is a no-op.** No exception, no state change. A
  late timer tick or a double tap cannot corrupt a finished session.
- `INV-SES-7` — **Degenerate inputs complete immediately.** An empty pool or a non-positive count
  finishes at construction rather than hanging on a null image.
- `INV-SES-8` — **Time is monotonic and injected.** A `Func<TimeSpan>` clock, defaulting to a
  `Stopwatch`. Never `DateTime.Now`; never time derived from counting UI ticks. Wall-clock changes
  and dropped repaints must not alter how much time a pose gets or how much time is banked.
- `INV-SES-9` — **`CurrentImage` is null if and only if the session is complete.** During a break it
  is *already the next pose's image*, loaded under the overlay — the screen covers it rather than
  blanking it, and may rely on both halves of that.
- `INV-SES-10` — **A break never counts a pose, and never follows the last one.** The target is
  checked before entering `Break`, so a session ends on a pose, never on a rest.
- `INV-SES-11` — **A skip lands on the next pose, never on a break.** Skipping is the artist
  rejecting an image, not asking for a rest.
- `INV-SES-12` — **Drawing time excludes break, background, and paused time.** The session clock
  stops for the whole break and for the whole pause.
- `INV-SES-13` — **A tick reports the transition it made.** `Tick()` returns exactly one of:
  nothing happened, a pose started, a rest started, the session completed. A caller never has to
  infer the transition from the state afterwards — which is what kept "what counts as a new pose"
  in an Activity, out of reach of every test.

**Rules — the pose clock**

- `INV-CD-1` — **Remaining is computed from the clock, never decremented by ticks.** Remaining is
  `duration − time actually spent running`. A slow, dropped, or bursty repaint cannot make a pose
  longer or shorter.
- `INV-CD-2` — **Paused time does not count.** This is what makes backgrounding correct: a hidden
  app burns no pose time and fires no timer.
- `INV-CD-3` — **`Pause` and `Resume` are idempotent**, and a resume can never revive a dead pose:
  resuming an expired phase leaves it expired, so the next tick retires it rather than granting more
  time. `Resume` still clears the paused state — a phase that expired while paused must not strand
  the session at `0:00`.
- `INV-CD-4` — **Remaining is never negative**, and expiry is `Remaining <= 0`.
- `INV-CD-5` — **Display rounds up.** A fresh 30 s pose reads `30` immediately and reads `0` only
  once it has actually expired. Format is `m:ss`, or `h:mm:ss` past an hour.
- `INV-CD-6` — **Advancing always restarts the clock running**, with all banked time cleared and
  the full configured duration (`INV-POSE-2`, `INV-POSE-3`).
- `INV-CD-7` — **It renders text, not views.** The session produces a string; the screen owns the
  repaint loop and the `TextView`.
- `INV-CD-8` — **A pause remembers why.** `Pause(PauseReason.User)` is the drawer's own pause and
  survives a lifecycle pause/resume cycle; only an explicit `Resume` clears it (`PausedByUser`). The
  screen binds the pause sheet to that, not to `IsPaused`, so backgrounding never resumes a pose the
  drawer stopped and never leaves the sheet up over a running one.

**Rules — resolving an id to an image**

- `INV-PLY-1` — **Loading is injected.** A `Func<string, TImage?>` returning null for "cannot
  read". The generic parameter is what keeps `Bitmap` out of the domain.
- `INV-PLY-2` — **An unreadable image is skipped, not counted.** It travels the skip path, so it
  neither advances the completed count nor banks time — a broken file cannot consume a slot the
  user paid for.
- `INV-PLY-3` — **Failure is bounded.** After a number of consecutive failures the session ends and
  reports "could not display" rather than looping. Necessary because a pool smaller than the target
  count repeats forever by design. The bound is the caller's to state — the player passes twice the
  pool, which cannot be reached while a drawable image remains even when a run of failures spans a
  pass boundary — and defaults to 100 only for a caller that says nothing. With `INV-PLY-8` the
  *loads* such a run costs are bounded by the pool rather than by the budget.
- `INV-PLY-4` — **"Could not display" is distinguishable from normal completion**, so the screen
  can show an error instead of a summary.
- `INV-PLY-5` — **The loader never throws through the session.** Decode failures are caught at the
  adapter and returned as null.
- `INV-PLY-6` — **Resolution is synchronous and re-entrant-safe.** It runs to a decision — a
  displayable image, completion, or the failure budget — before returning.
- `INV-PLY-7` — **The session may be asked what comes next.** `UpcomingImageId` reports the next id
  in the current pass without consuming it: it never advances the sequence, starts a clock, or
  counts anything. Refilling a drained pass is the one mutation it is allowed — a pass is
  materialised whole, so the resulting sequence is identical either way. Stated as an exception
  because `INV-SES-1` and `INV-X-12` otherwise forbid a query that mutates.
- `INV-PLY-8` — **An unreadable image is not loaded twice in one session.** Once the loader has
  reported an id unreadable, later passes skip it without a second load attempt. It still travels
  the skip path and still counts against the budget (`INV-PLY-2`, `INV-PLY-3`) — only the load is
  elided. Per session, never persisted: a file fixed between sessions must load again.

**Rules — the totals**

- `INV-SUM-1` — **The totals are a snapshot, not a live view.** Reading them never mutates
  anything.
- `INV-SUM-2` — **`ImagesDisplayed` is the completed count**, so skipped images are absent from it
  by definition and are reported separately as `SkippedCount`.
- `INV-SUM-3` — **`TotalDrawingTime` is banked time only** — completed poses, plus the final
  partial pose when the session was ended early. It is not wall-clock session length, and the
  difference is the point.
- `INV-SUM-4` — **They are readable in every phase** and simply reflect progress so far.

**State machine**

```
 evaluate ──▶ [Draft] ──construct with pool──▶ [Pose]
                  │                           │  │
                  │                           │  └──Skip──▶ [Pose]   (no count, no time banked)
                  │                           │
                  │        Next (count < target, break > 0) ──▶ [Break] ──Tick expiry──▶ [Pose]
                  │                           │
                  │                           ├──Next (count reaches target)──▶ [Complete]
                  │                           ├──End────────────────────────────▶ [Complete]  (partial time banked)
                  │                           └──failure budget exhausted───────▶ [Complete]  (CouldNotDisplayImage)
                  │
                  └──constructed with an empty pool / count <= 0──▶ [Complete]

 [Pose] ──Pause──▶ clock frozen ──Resume──▶ [Pose]        (INV-CD-2, INV-SES-12)
 [Complete] ──any command──▶ [Complete]                   (no-op, INV-SES-6)
```

**May not** — decode an image, know what a `Bitmap` is, read settings, write to storage, start a
thread, or format anything for display beyond the countdown string.

### 4.2 `ViewerTools` — *Implemented*

Entity owning how the pose is *presented*: grayscale, flip, grid, blur, and the zoom range. Kept
out of `DrawingSession` deliberately — nothing it holds affects what the session counts or how long
a pose lasts, and it survives independently of any pose.

**Rules**

- `INV-VIEW-1` — **A viewing aid never touches the count or the clock.** Toggling grayscale
  mid-pose changes pixels, nothing else.
- `INV-VIEW-2` — **Zoom is clamped to `[MinZoom, MaxZoom]`** and `CanZoomIn` / `CanZoomOut` are
  true only when a step would actually move it. `MinZoom` is 1.0 because 1.0 *is* fit-to-screen:
  below it the pose is letterboxed inside its own frame, which is a smaller image and no more of
  anything. The clamp also rounds, because zoom moves in fixed steps and stepping through binary
  fractions accumulates the drift that makes `0.1 + 0.2` famous — unrounded, repeated stepping
  reaches a value near a bound that never equals it, so the button at that end never disables.
- `INV-VIEW-3` — **Toggles are pure flags.** The entity holds no bitmap, no matrix, and no view;
  the screen reads the flags and renders. How an aid *looks* is therefore not held here either: the
  rule-of-thirds guides take their tone from the pose beneath them, and that decision lives in
  `GridContrast` — a supporting rendering service
  ([DDD-ARCHITECTURE.md §16](DDD-ARCHITECTURE.md#16-bounded-contexts)), not in this entity and not in the
  session. `ViewerTools.Grid` still answers the only question the domain asks: is the grid on.
- `INV-VIEW-4` — **Every aid persists across poses within a session**, zoom included: nothing is
  reset when the image changes. The starting state is not fixed either: grayscale is seeded at
  construction from `Settings.GrayscaleMode` (`INV-SET-P*`), so "start in grayscale" is honoured by
  the entity rather than by a screen toggling a flag once the session is already running. `ResetZoom` exists for a screen that wants a per-pose reset, and the
  player does not call it — see [DDD-ARCHITECTURE.md §20](DDD-ARCHITECTURE.md#20-where-the-code-deviates-today)
  before changing that, because it is a behaviour decision, not an omission.

---

## 5. Preferences objects

### 5.1 `Settings` — *Implemented*

The persisted preferences — pose duration, session image count, break, shuffle, grayscale, keep
awake, chime, last library — together with the single-document store that reads and writes them.
One object: the document has exactly one store and the store has exactly one document, so the split
bought a second name and no second implementation.

`Settings.Open(path)` reads the document, creating a defaulted one on first run; `Save()` upserts
it; `Dispose()` closes the database. Saving through a disposed instance throws rather than silently
dropping the write — losing preferences quietly is worse than failing loudly.

**Rules — the document**

- `INV-SET-P1` — **Exactly one document, forever.** Fixed identity (`Id == 1`) in one collection.
  A second document or collection needs a stated reason.
- `INV-SET-P2` — **Every property has a default**, so a first run and a missing field behave
  identically. New preferences are new defaulted properties, never a migration.
- `INV-SET-P3` — **Settings seed, they do not control.** Values are copied into the setup screen on
  launch and into intent extras on Start. Neither a session nor the setup logic reads settings at
  runtime.
- `INV-SET-P4` — **Written at named moments only** — a folder was picked and loaded successfully,
  Start was pressed, a settings toggle was flipped, a break preset was tapped, the screen was left
  (`OnPause`). Never on a keystroke, and never from a background thread. Leaving the screen is the
  backstop for how apps actually end: swiped off the recents list or reclaimed while backgrounded,
  neither of which runs `OnDestroy`.
- `INV-SET-P5` — **`LastCollection` holds a library reference, not its contents** (`INV-GRP-1`),
  and a stale one is expected (`INV-GRP-5`). The reference is what a launch restores *and* where
  the picker reopens — pointing the picker needs no grant, restoring the library does. What makes a
  stored value usable at all is `LibraryReference` (`INV-REF-1`..`INV-REF-3`), not the screen's
  judgement.
- `INV-SET-P6` — **Losing it is survivable.** A deleted or corrupt database costs preferences and
  nothing else; the app must start with defaults. Discarding one means discarding the write-ahead
  log beside it (`<name>-log<ext>`) in the same breath: LiteDB folds that log back in on open, so a
  log left behind by a process kill — or carried in by a device restore, written by an install that
  is not this one — is recovered into the fresh database, and a document nobody here wrote is
  deserialized as if it were ours. Delete both or neither.

  That a database *was* discarded is worth reporting: `Settings.Discarded` says so. It is static
  because it describes the file rather than the document, and the document it would otherwise live
  on is precisely the one that was thrown away. A preference set that quietly resets itself is
  invisible in a bug report unless something says it happened.

**Rules — the store**

- `INV-STO-1` — **Sole owner of the database.** No other type opens a `LiteDatabase`.
- `INV-STO-2` — **It owns the document identity.** Callers never set `Id`; it is stamped on write.
- `INV-STO-3` — **Disposed with the screen that opened it.**
- `INV-STO-5` — **A save that returned is a save that survives.** `Save()` checkpoints before it
  returns, so the value is in the datafile and not only in the write-ahead log. A process killed
  mid-write can leave that log truncated, and a truncated log does not fail to open — it reads back
  as the values from *before* the save, which is a preference silently reverting with nothing thrown
  and nothing logged.
- `INV-STO-4` — **Storage vocabulary stops here.** Nothing above it speaks BSON, collections, or
  LiteDB types. Merging the document and the store does *not* license spreading BSON attributes
  further: if `Settings` ever grows domain behaviour, split it into a domain type and a persisted
  DTO rather than decorating the model.

---

## 6. Proposed objects

### 6.1 `SessionRecord` — *Proposed*

A finished session kept for history or streaks. Not in the MVP; specified so it is not invented
ad hoc.

**Rules if built**

- Identity is a generated id assigned at completion; a session in progress has none.
- Immutable once written — a finished session is a fact, never edited.
- Stores the totals, the config used, the library reference, and a completion timestamp. Never the
  image ids: the library may have changed, and reconstructing an old sequence would be a lie.
- Timestamps enter from outside the domain, through the injected clock, exactly like every other
  time value.
- Its arrival is also the trigger for materializing domain events
  ([DDD-ARCHITECTURE.md §18](DDD-ARCHITECTURE.md#18-domain-events)) — a second consumer is the whole reason
  the events would exist.

---

## 7. Cross-object rules

These bind the objects together and are the ones most easily broken by a plausible-looking change.

**Reference and ownership**

- `INV-X-1` — Reference Library never references Session Execution. The pool flows one way.
- `INV-X-2` — Session Execution never reads `Settings`. Every per-session value arrives through
  `SessionConfig` or an explicit constructor argument.
- `INV-X-3` — No object holds another across a screen boundary. Screen-to-screen hand-off is intent
  extras carrying primitives, never a static, singleton, or `Application` field.
- `INV-X-4` — Nothing in the domain holds an Android object, a view, or a decoded bitmap. The
  session holds `TImage`, which is a type parameter, not a type.

**Identity and equality**

- `INV-X-5` — Value objects (`SessionConfig`, `DocumentEntry`, the pose id) are compared by value
  and never mutated after construction.
- `INV-X-6` — Entities with identity — `ReferenceLibrary` (root document id), `Settings` (fixed id)
  — are compared by that identity. `DrawingSession` and `ViewerTools` have no identity and must never be
  put in a set, dictionary key, or equality check.

**Time and randomness**

- `INV-X-7` — Every time-dependent object takes an injected `Func<TimeSpan>` monotonic clock.
  `DateTime.Now` and a directly constructed `Stopwatch` inside the domain are violations.
- `INV-X-8` — Every random-dependent object takes an injected `Random`, so any sequence is
  reproducible from a seed.
- `INV-X-9` — Nothing in the domain sleeps, posts, schedules, or starts a thread. The domain is
  synchronous; the screen owns the loop.

**Failure**

- `INV-X-10` — A single bad image never ends anything but its own pose (`INV-PLY-2`); a wholly bad
  pool ends the session with a distinguishable error (`INV-PLY-3`).
- `INV-X-11` — Every failure the platform can produce — revoked permission, missing provider,
  decode error, a persisted reference that no longer parses — has a defined domain outcome.
  "Throws" is not a defined outcome. A reference too broken to restore leaves the empty state
  showing; one too broken to point the picker at leaves the picker opening where it would have
  anyway. Neither costs the artist the screen they asked for.

**Background work**

- `INV-X-13` — **A library load is abandonable.** Only the most recent load may write the pool; a
  load that has been superseded *or abandoned* — the screen stopped, was destroyed, or reset — is
  discarded, including anything it decoded and including its failures. Filed in the cross-cutting
  family rather than `INV-GRP-*`: the rule is about the Android-side loader, and §8 maps
  `INV-GRP-*` to `ReferenceLibrary` / `ReferenceLibraryTests`, which cannot reach it. The walk an
  abandoned load leaves behind is a *partial* pool: legal because membership is only ever whatever
  the tree reports now (`INV-GRP-1`) and a query that yields nothing is the answer the adapter
  already gives a revoked grant (`INV-TREE-4`) — and safe only because it is discarded, never shown.

**Consolidation**

- `INV-X-12` — Merging objects never merges responsibilities. A rule that was enforced in one place
  before is enforced in one place after; a bigger aggregate is not licence for a screen to reach in,
  for a query to mutate, or for two concerns to become interdependent inside it. The measure is
  still §8: one invariant, one enforcement point, one test.

---

## 8. Enforcement

Each invariant above is enforced in exactly one place, and tested at the cheapest tier that can
reach it ([ARCHITECTURE.md §11](ARCHITECTURE.md#11-testing-strategy)). An object with several
invariant families has one test file per family rather than one per type.

| Invariant family | Enforced in | Tested by |
|---|---|---|
| `INV-IMG-*`, `INV-GRP-*`, `INV-POOL-*`, `INV-TREE-*` | `ReferenceLibrary` + the SAF adapter | `ReferenceLibraryTests` with an in-memory tree. `INV-GRP-1`'s re-derivation is *triggered* by `MainActivity.OnStart`, covered on a device by `FolderPickerUiTests.ReturningFromASession_RebuildsTheGridAndPicksUpNewImages` |
| `INV-SET-1..5`, `INV-CFG-*` | `SessionSetup`, `DrawingSession<TImage>.Evaluate` | `SessionSetupTests`, `DrawingSessionSetupTests` |
| `INV-SES-1..9`, `INV-SUM-*` | `DrawingSession<TImage>` | `DrawingSessionTests` |
| `INV-SES-10..13`, `INV-POSE-*` | `DrawingSession<TImage>` | `DrawingSessionBreakTests`, `DrawingSessionTimeAccountingTests` |
| `INV-CD-*` | `DrawingSession<TImage>` | `DrawingSessionCountdownTests` |
| `INV-PLY-*` | `DrawingSession<TImage>` | `DrawingSessionImageTests` with a fake loader |
| `INV-VIEW-*` | `ViewerTools` | `ViewerToolsTests` |
| `INV-SET-P*`, `INV-STO-*` | `Settings` | `SettingsTests` |
| `INV-STO-5` | `Settings.Save` (checkpoint) | `SettingsTests` kill + truncation cases, `FolderPickerUiTests.PickedFolder_SurvivesTheProcessBeingKilled` |
| `INV-REF-*` | `LibraryReference`, wired by `MainActivity` | `LibraryReferenceTests`, `FolderMemoryContractTests`, `FolderPickerUiTests` |
| Cross-context flows | The objects together | `SessionE2ETests` |
| `INV-X-13` (an abandonable load) | `LoadGeneration` and `LibraryLoadState` (the "never a mixture" clause), called by `LibraryLoader` and `MainActivity` | `LoadGenerationTests` and `LibraryLoadStateTests` for the rules themselves; `LibraryLoadContractTests` for where they are called — not observable through Appium, see #4 |
| `INV-X-*` (the rest) | Structural | Project references, `AndroidBuildTests`, `SessionScreenContractTests`, `FolderMemoryContractTests`, `CrossActivityContractTests`, code review |
| `INV-SET-P4`, `INV-STO-1` | Structural (Activity isolation) | `CrossActivityContractTests` (SessionActivity never touches Settings; the folder persistence chain contains no await) |

Adding an invariant means adding a named test. Removing one means saying so in a ticket — an
invariant deleted quietly is how a domain model stops describing the code.

---

## 9. Consolidation

The catalogue was fifteen objects for an app of roughly 2,000 lines. Four merges took it to nine.
This section is the map from the old names to the new, and the state of the code against it.

| Merged object | Absorbed | Why they are one concept |
|---|---|---|
| `Pose` | `ReferenceImage`, `Pose` | An image the app never shows has no domain meaning, and a pose is always a pose *of* an image. They shared an identity already — the id |
| `ReferenceLibrary` | `ImageGroup`, `ImagePool`, `FolderImageEnumerator` | The pool is the group's contents and the enumerator is how it computes them. Two of the three held no state; one had no independent lifetime |
| `DrawingSession<TImage>` | `SessionSetupState`, `DrawingSession`, `SessionPlayer<TImage>`, `PoseCountdown`, `SessionSummary` | Every piece changes only when the session advances. The setup state is the session before it starts; the summary is a projection of counters it already holds; the player and the countdown each existed to serve exactly one session and nothing else |
| `Settings` | `AppSettings`, `SettingsStore` | One document, one store, one collection, forever. The split bought a name, not a seam — the port that would justify it (`ISettingsRepository`) does not exist |

The session merge absorbed **six** types, not five: `SessionSetupState`, `DrawingSession`,
`SessionPlayer<TImage>`, `PoseCountdown`, `PoseSession<TImage>` and `SessionSummary`.

**What did not merge, and why**

- `IDocumentTree` / `DocumentEntry` — the anti-corruption layer. Folding a port into the aggregate
  it protects removes the seam that keeps SAF out of Core (`INV-TREE-1`).
- `SessionSetup` / `SessionConfig` — the published contract between two contexts. Config is the one
  object that crosses the intent-extra boundary, and it must stay small enough to serialize as
  primitives (`INV-CFG-3`).
- `ViewerTools` — presentation state with its own lifetime, touching nothing the session counts
  (`INV-VIEW-1`). Merging it would put "is the grid on" inside the object that owns the clock.

**Code status: done.** The Core types match the model. The merge itself moved code rather than
rewriting behaviour, but the model did not come through unchanged — §8 requires any added, changed
or removed rule to be stated, so here they are:

| Rule | Change | Why |
|---|---|---|
| `INV-VIEW-1..4` | **Added** | `ViewerTools` is a real Core type that the fifteen-object catalogue never listed. The rules describe what it already did |
| `INV-SES-10`, `INV-SES-11`, `INV-SES-12` | **Added** | The break's rules were enforced by `PoseSession` and written down nowhere |
| `INV-POOL-2` | **Newly enforced** | The library de-duplicates ids as it walks. The old static enumerator asserted this and did not do it |
| `INV-CD-6` | **Rewritten** | Was "`Restart` always resumes running", naming a public command that no longer exists; now "advancing always restarts the clock", which is the same rule at the surface that survived |
| `INV-POSE-3` | **Moved** | Was enforced by `SessionActivity.Advance`, then by `PoseSession`, now by the session aggregate. The rule never changed; its home did, twice |
| `INV-SET-2` | **Clarified** | It never accounted for `BreakSeconds = 0` being legal, which it always was |
| `INV-SES-9` | **Corrected** | It claimed `CurrentImage` is null during a break. It never was — the next pose's image is loaded under the overlay |
| `INV-VIEW-4` | **Weakened to match the code** | It claimed zoom resets per pose. Nothing has ever called `ResetZoom` |
| `INV-SET-P4` | **Corrected** | Two write moments were listed; there have always been four |

Changed again by the repo-wide review of 2026-08-16:

| Rule | Change | Why |
|---|---|---|
| `INV-CD-3` | **Corrected** | It said `Resume` is a no-op once expired. It was, and that stranded the session: a pause taken in the fraction of a second after a phase hit `0:00` left `IsPaused` true forever, and `Tick` bails while paused. A resume now un-pauses without granting time, so the next tick retires the pose. Covered by `DrawingSessionCountdownTests` |
| `INV-CD-8` | **Added** | The screen decided "did the drawer pause deliberately?" by reading a widget's `Visibility`, a lifecycle rule no test could reach. `PauseReason` / `PausedByUser` move it into the aggregate |
| `INV-POOL-6` | **Added** | The whole pool crossed to the player as an intent extra, throwing `TransactionTooLargeException` on a DCIM-sized folder — uncaught, and reproducing on every launch because the folder is persisted |
| `INV-GRP-6` | **Narrowed** | It said randomization never belongs to the library. `INV-POOL-6` is one random choice made there — of membership, never of order |
| `INV-IMG-4` | **Corrected** | It claimed decoded images were bounded to `MaxImageDimension`. The sampler bounded the *short* side, so an aspect-extreme source was effectively unbounded; the long side is now held to within 2x the ceiling |

Changed by #6 and #5:

| Rule | Change | Why |
|---|---|---|
| `INV-SES-13` | **Added** | `Tick()` returned a bool, so the screen reconstructed the transition from the state left behind (`if (!session.OnBreak) Chime();`) — a rule about what counts as a new pose, living where no unit test could reach it. `INV-SES-10` and `INV-CD-6` are what the `SessionTick` enum makes observable; neither changed in meaning |
| `INV-PLY-7` | **Added** | The screen could not decode ahead without asking what came next, so a multi-megabyte decode ran inside the 200 ms repaint callback at every pose boundary |
| `INV-PLY-8` | **Added** | A pool smaller than the configured count repeats by design, so an unreadable file was re-opened and re-decoded on every pass — up to the whole failure budget inside one tick |
| `INV-PLY-3` | **Unchanged in meaning** | The budget is now an explicit value at the player's call site rather than the constructor's implicit default of 100, and is derived from the pool so it scales with the run |
| `INV-POOL-6` | **Narrowed** | The bound was a flat 1000 sized only by the Binder buffer. It is now derived from the session's length as well, because the player is handed an array and cannot re-sample. The rule moved to `SessionSetup`, where a config-derived number belongs; the library still owns the sampling |
| `INV-SES-1` | **Exception stated** | "Commands only" now carries one named carve-out: `UpcomingImageId` may refill a drained pass in order to answer (`INV-PLY-7`). It changes nothing else, and a pass is materialised whole, so the sequence is identical either way |
| `INV-X-12` | **Exception stated** | Same carve-out from the other side: a query that mutates was forbidden outright, and is now forbidden except where the mutation is stated as part of the query's own rule |

Changed by #8:

| Rule | Change | Why |
|---|---|---|
| `INV-SET-P4` | **Tightened** | Save deferred past a successful load; a folder that fails to load is no longer persisted (#8) |

Changed by #12:

| Rule | Change | Why |
|---|---|---|
| `INV-GRP-3` | **Widened** | It named only the visited set, which stops a provider reporting a cycle and nothing else. The depth ceiling is a second, non-subsuming guard against one that mints a fresh id per level. The rule shipped and was tested, but lived only in a `ReferenceLibrary.cs` comment |
| `INV-POOL-6` | **Widened** | It described the length bound without the single-id case, and did not say that the bound trims the tail of an ordered sample rather than re-drawing it. Same provenance: enforced and tested, documented only in a comment |

Neither is a change to behaviour. Both rules already shipped and were already covered by
`ReferenceLibraryTests`; #12 deleted the comments that were their only record, so the record moved
here first. Both belong to families that already have a §8 enforcement row, so §8 is unchanged.

Changed by #13:

| Rule | Change | Why |
|---|---|---|
| `INV-REF-1` | **Widened** | It named `MaxLength` without saying what the ceiling is for. The bound is a Binder-transaction bound, not a guess at folder-name length |
| `INV-REF-4` | **Widened** | It said which grants are released and not in what spelling. A grant is handed back as the platform reported it, never canonicalised |
| `INV-REF-5` | **Widened** | It named the four states without saying that a failed walk reaches `Unavailable` on its own, even while the grant is still listed |

As with #12, none of this is a change to behaviour: all three shipped, all three are covered by
`LibraryReferenceTests`, and all three were recorded only in the comments #13 deleted. The
`INV-REF-*` family already has a §8 enforcement row, so §8 is unchanged.

Changed by #14:

| Rule | Change | Why |
|---|---|---|
| §3.1 card | **Four sentences added** | What `EstimateSeconds` counts, that `HandoffBound` is total across the whole `int` range, why its multiple is greater than one, and that the presets carry their own render order. All four shipped, all four are covered by `SessionSetupTests`, none was written down outside `SessionSetup.cs` comments |

No invariant is added or changed: these are card sentences, not new `INV-SET-*` rules, so the §8
enforcement row for the family stands as it is.

Changed by #16:

| Rule | Change | Why |
|---|---|---|
| `INV-VIEW-2` | **Widened** | It named the bounds without saying that 1.0 *is* fit-to-screen, or why the clamp rounds. Unrounded stepping drifts and a bound is never reached exactly, so the button at that end never disables |
| `INV-VIEW-4` | **Widened** | It said nothing is reset between poses, which reads as if the starting state were fixed. Grayscale is seeded at construction from `Settings.GrayscaleMode` |

Both shipped, both are covered by `ViewerToolsTests`, and both were recorded only in the comments
#16 deleted. The `INV-VIEW-*` family already has a §8 enforcement row, so §8 is unchanged.

Changed by #17:

| Rule | Change | Why |
|---|---|---|
| `INV-SET-P6` | **Widened** | It said losing the database is survivable without saying what discarding one has to delete. The write-ahead log goes with the datafile, because a log left by a kill — or carried in by a device restore — is folded back into the fresh database on open. It also now names `Settings.Discarded` and why that flag is static |

Both halves shipped and are covered by `SettingsTests`; neither was written down outside
`Settings.cs`. The `INV-SET-P*` / `INV-STO-*` families already have §8 enforcement rows, so §8 is
unchanged.

One behaviour did change, deliberately: when the consecutive-failure budget is exhausted the session
now banks **no** partial time for the unreadable image it died on. The old `SessionPlayer` routed
that path through `End()`, which banked it — time spent failing to decode, counted as drawing time,
against `INV-PLY-2`.

| Merged object | Was | Is |
|---|---|---|
| `Pose` | `string` ids + session fields | Unchanged: still implicit. It gets a type when a second string-shaped concept enters the same signatures (the `ImageRef` candidate, [DDD-ARCHITECTURE.md §17](DDD-ARCHITECTURE.md#17-tactical-model)) |
| `ReferenceLibrary` | `FolderImageEnumerator` (static) + the tree URI and a `List<string>` held loose in `MainActivity` | `ReferenceLibrary.cs`, holding root id, display name and pool, with `IDocumentTree` / `DocumentEntry` alongside it |
| `DrawingSession<TImage>` | `PoseSession<TImage>` composing `DrawingSession` + `SessionPlayer<TImage>` + `PoseCountdown`, with `SessionSummary` as a projection | `Session/DrawingSession.cs`: one aggregate, the other four as private state and queries, plus the non-generic `DrawingSession.Format` |
| `Settings` | `AppSettings` + `SettingsStore : IDisposable` | `Data/Settings.cs`: one type with `Open` / `Save` / `Dispose` |

Three consequences worth knowing before the next change:

- **The draft is a session, so `Evaluate` is generic.** `MainActivity` calls
  `DrawingSession<PoseImage>.Evaluate(...)` for a screen that never touches an image. That reads oddly
  and is the accepted price of deleting `SessionSetupState`; the alternative is that record under a
  new name.
- **`INV-POOL-2` is now enforced, not just asserted.** The library de-duplicates ids as it walks, so
  a provider that reports one document under two parents yields one pool entry. The old static
  enumerator would have listed it twice.
- **`Settings` collides with `Android.Provider.Settings`.** `MainActivity` carries a
  `using Settings = FigureDrawing.Data.Settings;` alias. Any new Android file that touches
  preferences needs the same alias or a qualified name.
