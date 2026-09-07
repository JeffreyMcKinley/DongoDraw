---
name: prd-generator
description: Write a requirements doc for a FigureDrawing feature — a ticket filed as a GitHub issue, or a longer PRD for a multi-ticket feature. Use when the user asks to "create a PRD", "write requirements", "spec this feature", "write a ticket", or "document a feature" before implementation. Produces docs that use the repo's ubiquitous language, place the work in a bounded context, split it across Core/Android, and name its test tier.
---

# PRD Generator — FigureDrawing

Turn a feature idea into a requirements doc that the implementation checklist in
[ARCHITECTURE.md §13](../../../docs/ARCHITECTURE.md) can be run against directly.

This is a single-user, offline, on-device Android app. No server, no accounts, no telemetry,
no A/B tests. A PRD here answers *what rule are we adding, which object owns it, and which
test proves it* — not adoption curves. Strip out anything that assumes a backend or an
analytics pipeline.

Read [docs/ARCHITECTURE.md](../../../docs/ARCHITECTURE.md) and
[docs/DOMAIN-MODEL.md](../../../docs/DOMAIN-MODEL.md) before writing. A PRD that contradicts
them is wrong, not visionary.

## 1. Pick the format

| User asks for | Write | Where |
|---|---|---|
| one story, fits one implementation pass | **Ticket** | one GitHub issue |
| a feature spanning several tickets | **Feature PRD** + child tickets | one tracking issue with a task list + one issue per child |
| "quick spec", "one-pager", exploration | **One-pager** — problem, approach, acceptance criteria only | scratchpad, or an issue if it will be built |
| a rework of existing behaviour | **Ticket** citing the invariants it changes | one GitHub issue |

Default to the single ticket. It is the format the repo already uses (`gh issue view 4` is the
worked example) and it is smaller than a PRD for a reason.

The ticket id is the issue number GitHub assigns — do not invent one. See
[docs/agents/issue-tracker.md](../../../docs/agents/issue-tracker.md) for the `gh` conventions.

## 2. Gather context

Ask only what you cannot infer from the repo. Read `README.md`, `gh issue list --state all`, and
the relevant Core type first — half these answers are already written down.

**Discovery questions:**

```
1. What is the artist trying to do that they cannot do today?
2. Which part of the flow does this touch — picking a folder, setting up, or running a session?
3. Does it change what counts (poses, drawing time), or only how the pose is presented?
4. Is it configurable? If so, does it persist across launches or is it per-session?
5. What should happen at the edges — empty pool, unreadable image, session already complete,
   pause/background, fold/unfold?
6. What is explicitly not in this change?
```

Question 3 is the load-bearing one: **counting vs presenting** decides whether the rule lands in
the session aggregate (`DrawingSession<TImage>`) or in `ViewerTools`, and whether it needs new
invariants or none.

If the user hands over a detailed brief, skip straight to §3 and ask only about edges and scope.

## 3. Place it in the model

Every PRD states this before it states requirements. Fill the table:

| Field | Answer |
|---|---|
| Bounded context | Reference Library / Session Setup / Session Execution / Preferences / Rendering (supporting) |
| Owning object | one of the ten in [DOMAIN-MODEL.md §1](../../../docs/DOMAIN-MODEL.md) |
| New Core type? | usually **no** — justify a new *domain* object against [DOMAIN-MODEL.md §9](../../../docs/DOMAIN-MODEL.md). A supporting type (pure, Android-free, testable — see ARCHITECTURE.md §3/§16) is a smaller decision and does not join the catalogue |
| New invariants | `INV-<FAMILY>-<n>`, using the existing families (`SES`, `POSE`, `POOL`, `GRP`, `SET`, `CFG`, `VIEW`, `STO`, …) |
| Invariants changed | cite by id; a changed invariant is a breaking change and must say so |
| Crosses a context boundary? | if yes, name the contract (`SessionConfig`, the pool, an intent extra, a `Settings` property) |

**Ubiquitous language is not optional.** Use the terms from
[ARCHITECTURE.md §15](../../../docs/ARCHITECTURE.md): reference image, reference library, pool,
pass, pose, session, setup, config, complete, skip, break, viewing aid, drawing time, unreadable,
settings. A synonym in a PRD ("slide", "workout", "gallery", "filter") becomes a synonym in code.

Two distinctions to get right in the prose:

- **Config vs Settings** — config is validated, immutable, one session. Settings is persisted,
  mutable, one installation.
- **Complete vs Skip vs End** — three terminal moves on a pose, three different effects on
  `CompletedCount` and drawing time.

## 4. Write the stories

Standard form, one per user-visible capability:

```
As an artist,
I want to [action],
So that [benefit].

Acceptance criteria:
- [ ] [observable, testable behaviour]
- [ ] [edge case]
- [ ] [what must NOT change]
```

Rules for this repo:

- The user is **the artist**, drawing alone, offline. There is no admin, no team, no second role.
  Do not invent personas.
- Acceptance criteria are checkbox lines, phrased so a unit test name falls out of them.
- Every story that touches counting names its effect on `CompletedCount`, `SkippedCount`, and
  `TotalDrawingTime` — including "unchanged".
- At least one criterion should be a negative: the invariant this change must not break.

## 5. Name the split and the test tier

A PRD that does not say which side of the Core/Android line each piece lands on is unfinished.

| Piece | Goes in | Proven by |
|---|---|---|
| A rule, calculation, or state machine | `FigureDrawing.Core` | unit test in `FigureDrawing.Tests` |
| A platform need of that rule (SAF, bitmaps, clock, randomness) | injected abstraction or delegate ([ARCHITECTURE.md §4](../../../docs/ARCHITECTURE.md)) | in-memory fake |
| View wiring, rendering, lifecycle | Activity | contract test for new view ids / strings |
| A whole flow across contexts | — | E2E-model test (`SessionE2ETests`) |
| Behaviour genuinely unreachable from Core | Activity | Appium test — last resort, justify it |

State per story: **Core type + method**, **Activity wiring**, **new view ids / strings**, **test
tier**. If a story has nothing in Core, say why — that is usually the signal the rule was put in
the wrong place.

Also flag, when the change touches them:

- new user-facing text ⇒ `Resources/values/strings.xml` **and** `UiResourceContractTests`
- new control ⇒ Nocturne styles in `Resources/values/styles.xml`, never an inline
  `android:background`
- new session input ⇒ a new public `Extra*` constant on `SessionActivity`, not a string literal
- new preference ⇒ a new property on `Settings` with a default, not a second collection
- anything time-based ⇒ injected `Func<TimeSpan>`, never `DateTime.Now` in Core

## 6. Acceptance signals, not metrics

There is no analytics pipeline. Do not write adoption, retention, conversion, or NPS targets —
nothing measures them and nothing ever will. Replace the metrics section with signals that can
actually be observed:

| Signal type | Example |
|---|---|
| Test | "every row of the new invariant table has a named unit test" |
| Contract | "`SessionScreenContractTests` asserts the new view id exists" |
| Suite health | "`./nx.bat run FigureDrawing.Tests:test` stays green" |
| On-device | "a 60-image folder runs a 20-pose session with no visible stutter on the emulator" |
| Budget | "peak decoded bitmap keeps its long side within 2x `MaxImageDimension` (1080 px)" |

Numbers still belong here — they just come from the device and the suite, not a dashboard.
"Loads in under 2 seconds" is fine; "20% lift in engagement" is not.

## 7. Scope, risks, open questions

**Out of scope** is the most valuable section in this repo — the app is ~1,200 lines and every
feature is one refactor away from becoming three. List explicitly what is not being built, and
what is deferred to a later FD ticket.

**Risks** — pull from the known costs in
[ARCHITECTURE.md §20](../../../docs/ARCHITECTURE.md) when the change goes near them:

- decoding still runs on the main thread in the repaint loop at a pose boundary (#5); the folder
  walk's half of this shipped in #4. A PRD that introduces background work must follow the shape
  in [ARCHITECTURE.md §7](../../../docs/ARCHITECTURE.md#7-threading) rather than inventing one, and
  must say where its work is abandoned and why
- the pool crossing to the player is bounded by `ReferenceLibrary.Sample` (`INV-POOL-6`), so a
  feature that widens what crosses has to re-check that bound
- session state is not saved across process death
- SAF folder grants are taken and never released; access can expire (`INV-GRP-5`)
- `MainActivity` already spans three contexts — anything added there makes that worse
- API 26 floor, and foldable layouts must survive a configuration change in place

**Open questions** — real unknowns only, each with who decides. A question nobody owns is
a decision that will be made accidentally during implementation.

## 8. Validate before handing it over

- [ ] Uses only the §15 vocabulary — no synonyms
- [ ] Names the bounded context and the owning object
- [ ] New or changed invariants listed with ids; changed ones flagged as breaking
- [ ] Every rule lands in Core, or the PRD argues why it cannot
- [ ] Every story has checkbox acceptance criteria, including a negative one
- [ ] Test tier named per story; nothing relies on Appium that Core could reach
- [ ] New strings / view ids call out their contract-test entries
- [ ] Acceptance signals are observable on-device or in the suite — no invented analytics
- [ ] Out of scope is explicit
- [ ] No implementation detail the tests do not require (leave the "how" open)
- [ ] Contradicts nothing in ARCHITECTURE.md §14 anti-patterns
- [ ] No placeholder text left

## 9. Ship it

1. Write the body to a scratchpad file using
   [references/ticket-template.md](references/ticket-template.md), or
   [references/prd-template.md](references/prd-template.md) for a multi-ticket feature. The H1 is
   the issue title and is not part of the body. Links in the body must be absolute
   (`https://github.com/JeffreyMcKinley/FigureDrawing/blob/master/docs/...`) — GitHub does not
   resolve relative paths in issue bodies.
2. File it: `gh issue create --title "<title>" --body-file <path> --label <state> --label <context>`.
   The state label is one of the five roles in
   [docs/agents/triage-labels.md](../../../docs/agents/triage-labels.md); the `context:*` label
   names the bounded context. For a multi-ticket feature, create the children first, then edit the
   tracking issue to hold a task list of their real numbers.
3. Report the issue number back to the user. Dependencies go in the body as `#N`, not a
   suggested-order table.
4. Do **not** start implementing. The ticket is the deliverable; wait for the go-ahead.

## Notes

- Anti-patterns for PRDs here mirror the code ones: a requirement that only an Activity could
  satisfy, a rule stated in terms of a widget, a persisted flag that duplicates `SessionConfig`,
  a story that needs a network call.
- The migrated tickets (#4, #5, #6) are the reference for tone and length. Match them; they are
  short on purpose.
