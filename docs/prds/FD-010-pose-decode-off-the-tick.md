# FD-010 — A pose boundary never blocks the repaint loop

Status: shipped

> **Shipped.** What was built differs from what is written below in six places, recorded here
> rather than quietly diverged from:
>
> 1. **The all-unreadable criterion is met by a route this ticket did not name.** A run of
>    unreadable ids is still resolved synchronously — but the resolve itself no longer happens on the
>    UI thread: the session is constructed off it, and `INV-PLY-8` caps the loads such a run costs at
>    one per distinct id. The budget's *value* is explicit at the call site as specified.
> 2. **The prefetch runs during breaks too.** The break's *displayed* image is already decoded, but
>    `UpcomingImageId` during a break is the pose-after-next — prefetching it costs nothing extra and
>    makes a skip during a rest instant.
> 3. **"Cancelled and its bitmap recycled" is really "abandoned".** A running `BitmapFactory` decode
>    cannot be stopped; the generation guard discards its result on the main thread once it returns.
> 4. **Bitmap ownership was in scope after all.** This added a second owner (the one-entry cache) and
>    a second thread that can produce a bitmap nobody wants.
> 5. **Contract was not `n/a`,** and the ticket grew a fourth part. Source-shape assertions pin the
>    prefetch wiring in both directions — that it is started, and that it is abandoned on every way
>    out — and `SessionSetup.HandoffBound` narrows `INV-POOL-6` so the pool-derived failure budget
>    scales with the run rather than with the folder.
> 6. **The session's construction moved off the UI thread too**, which the ticket left out of scope
>    by assuming only the boundary mattered. The running constructor resolves the first pose, and
>    that is a decode; the screen now shows a loading state while it happens. Guide sampling moved
>    onto the decode thread with it, so `TImage` is a `PoseImage` (bitmap + its samples) rather than
>    a bare `Bitmap`.

**Story:** _As an artist, I want the next pose to appear the instant the countdown reaches zero,
even when the reference images are large or some of them are unreadable._
**Depends on:** FD-003, FD-004, FD-005

## Summary

`DrawingSession.Tick()` resolves the next reference image synchronously, so a two-pass
`ContentResolver` open plus a multi-megabyte decode runs on the UI thread inside the 200 ms repaint
callback. A folder of unreadable images can chain up to the failure budget (100) decode attempts in
a single tick — past the ANR window on a slow provider. After this, the image for the next pose is
already decoded when the boundary arrives, and an image that proved unreadable is not retried on
every pass through the pool.

## Model placement

| | |
|---|---|
| Context | Session Execution |
| Owning object | `DrawingSession<TImage>` |
| New Core type | no — this adds one query to the session aggregate. A "prefetcher" type would own no state the session does not already own (DOMAIN-MODEL.md §9). |
| New invariants | `INV-PLY-7` — **The session may be asked what comes next.** `UpcomingImageId` reports the next id in the current pass without consuming it: it never advances the sequence, starts a clock, or counts anything. Refilling a drained pass is the one mutation it is allowed (a pass is materialized whole, so the resulting sequence is identical either way) — state that exception explicitly, because `INV-SES-1` and `INV-X-12` otherwise forbid a query that mutates. `INV-PLY-8` — **An unreadable image is not loaded twice in one session.** Once the loader has reported an id unreadable, later passes skip it without a second load attempt. This one belongs in Core with the rest of the family, not on the Activity — DOMAIN-MODEL.md §8 maps `INV-PLY-*` to `DrawingSession<TImage>`. |
| Invariants changed | `INV-PLY-3` (bounded failure budget) is unchanged in meaning, but the budget becomes an explicit value at the call site rather than the implicit default of 100. |
| Crosses a boundary | no. The loader stays the injected `Func<string, TImage?>`; nothing Android-shaped enters Core. |

## Approach

- **Core:** `DrawingSession<TImage>.UpcomingImageId` — a peek at the head of the upcoming queue
  (refilling the pass if it is drained), so the screen can decode ahead. Strictly a query: no phase
  change, no clock, no counting. The failure budget stays a constructor parameter; the player passes
  one explicitly.
- **Android:** `SessionActivity` decodes `UpcomingImageId` on a background thread *during* the pose
  and hands the result to the loader through a small one-entry cache, marshalled back through the
  existing ticker. The useful prefetch window is the pose, not the break — the break's image was
  already decoded by the tick that started the break. A per-session set of ids the loader could not
  read, owned by the session (`INV-PLY-8`), keeps a broken file to one round trip per session
  instead of one per pass.
  A prefetch in flight when the screen tears down is cancelled and its bitmap recycled.
- **Resources:** one new string for the loading state (`session_loading_text`), shown while the
  session is being built. No new view id — the existing `session_status` carries it.
- **Persistence:** none. An unreadable id is per-session knowledge, not a preference — a file fixed
  between sessions must load again.
- **Injected dependency:** none new. The existing loader delegate and clock stand.

## Acceptance criteria

- [x] Asking for `UpcomingImageId` leaves `CurrentImageId`, `CompletedCount`, `SkippedCount`,
      `TotalDrawingTime`, the phase and both clocks exactly as they were.
- [x] `UpcomingImageId` refills a drained pass, so it is non-null whenever the session will show
      another pose, and null once the session is complete.
- [x] A pose boundary attaches the already-decoded image without a decode on the boundary tick —
      with two bounded exceptions, both stated in note 1: a boundary arriving mid-decode waits for
      the decode already running rather than starting another, and a boundary that skips past an
      unreadable id decodes its replacement, because only one image is decoded ahead.
- [x] An id the loader reported unreadable is skipped on later passes without a second load
      attempt, and the skip still counts toward `SkippedCount` exactly as it does today
      (`INV-PLY-2`).
- [x] A pool in which every image is unreadable still ends in the error terminal state under the
      failure budget (`INV-PLY-4`), and does so without the UI thread blocking for the whole budget.
- [x] Pause, background and end during a prefetch leave no bitmap attached — and no callback that
      does anything: the continuation still runs (a queued one cannot be un-queued) but sees a stale
      generation, frees what it produced, and touches no view.
- [x] Prefetching never changes which image is shown or in what order — with a fixed `Random` and
      shuffle on, the sequence is identical to today's (`INV-SES-4`).

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | `DrawingSessionImageTests` (the `INV-PLY-*` home, DOMAIN-MODEL.md §8) — `UpcomingImageId` peeks without advancing, refills a drained pass, is null once complete; sequence with a seeded `Random` is unchanged by peeking. `INV-PLY-7`, `INV-PLY-8`. |
| Contract | `SessionScreenContractTests` — the prefetch is started (Render, OnResume) and abandoned on every way out, the decode leaves the UI thread, the session is built off it, the generation guard settles an abandoned decode, and the budget is stated. Its string contract gains `session_loading_text`. |
| E2E-model | `SessionE2ETests` — a run whose loader fails for a subset of ids completes with the same counts as today, with the loader called once per broken id. |
| UI (Appium) | `n/a` — the timing claim is measured on-device, but every rule here is reachable from Core. |

## Out of scope

- Decoding more than one pose ahead. One is the whole win; a queue is a cache, and `INV-IMG-4` says
  decoded pixels are not cached beyond the current screen.
- Persisting the unreadable-id set across sessions.
- Changing `Tick()`'s return type to a phase-change enum. Worth doing, but it rewrites eight
  existing test assertions and belongs in its own ticket.
- Bitmap ownership and recycling, which is already fixed on the current player screen.

## Risks

- Prefetching adds a second thread to the player screen, which currently has exactly one. Every path
  out of the screen (pause, background, end, "run it again", destroy) must cancel it and recycle
  whatever it produced, or this trades an ANR for a leak.
- The unreadable-id cache changes behaviour for transiently unreadable files: a file that becomes
  readable mid-session stays skipped until the next session.
- A smaller explicit failure budget changes when the error screen appears; pick it against the pool
  size, not the count.
- Main-thread decoding is recorded as accepted debt in ARCHITECTURE.md §20 — that entry needs
  updating when this lands.

## Open questions

- What explicit failure budget should the player pass — a fixed small number, or a fraction of the
  pool size? — decided by implementation, with the value stated in the code comment.
