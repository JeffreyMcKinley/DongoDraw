# FD-016 — Comment diet

> Multi-ticket feature. Child tickets carry the acceptance criteria; this document carries the
> shape and, in §2, the keep-list every child is judged against.

**Status:** ready-for-agent
**Child tickets:** FD-017 … FD-028

## 1. Problem

Production code in this repo is roughly 29% comment lines — 1,316 of 4,502 lines across the Core
types, the two Activities and the rendering services, before the 1,111 lines in
`FigureDrawing.Tests` and 317 in `FigureDrawing.UITests`. `DrawingSession.cs` carries 218 comment
lines, `SessionActivity.cs` 305, `MainActivity.cs` 287.

Most of it is not a comment on the code; it is the domain model restated in prose next to it. The
consolidation history in `DrawingSession.cs` is [DOMAIN-MODEL.md §9](../DOMAIN-MODEL.md#9-consolidation).
The rendering-service placement argument in `GridContrast.cs` is
[ARCHITECTURE.md §16](../ARCHITECTURE.md). The enum-member prose restates member names. Prose that
lives in two places drifts in one of them, and a reader scrolling for the rule cannot tell which
copy is current. Reading a 744-line file that is 30% essay costs more than reading the 526 lines of
code inside it.

This is a readability change, not a behaviour change. Nothing here alters what the app does.

## 2. Proposed shape

Delete comments file by file, one pull request per domain object, against a fixed keep-list. A
comment that states a rule survives only after the rule is confirmed present in
[DOMAIN-MODEL.md](../DOMAIN-MODEL.md) or [ARCHITECTURE.md](../ARCHITECTURE.md); if the rule is real
and documented nowhere, the PR adds it to the doc rather than losing it. Nothing else in the file
changes: the comment-stripped source before and after a PR is identical, except for renames made
under §2.3.

### 2.1 Delete

1. Restatements of the code — `// The guides sit on the thirds.` above `const double FirstThird`.
2. Section-divider banners — `// --- Run state: sequence and counts ---`.
3. Design-history and placement essays — why six types became one, why a type sits beside another.
   That is DOMAIN-MODEL.md §9 and ARCHITECTURE.md §16.
4. Any rule already stated in DOMAIN-MODEL.md or ARCHITECTURE.md under an invariant id.
5. Per-member prose on enum members and record fields whose name already carries it.

### 2.2 Keep (the keep-list)

1. A non-obvious *why* that a plausible edit would re-break and that lives nowhere else — two lines
   at most, citing its invariant id where it has one.
2. A platform trap a reader cannot infer from the code: the CRLF/`$`-anchoring note in
   `SourceContract`, the `ApplicationContext` note for `ContentResolver`
   ([ARCHITECTURE.md §14](../ARCHITECTURE.md)), bitmap recycling ownership.
3. `#pragma warning disable` justifications (`SettingsTests.cs:385`).
4. Test comments naming *why the rule exists* with an invariant id — mandated by
   [CLAUDE.md](../../CLAUDE.md) and out of scope for every child but FD-027.
5. One line of type-level header where the type's role is not readable from its name and namespace.
   One line, not a paragraph.

### 2.3 Rename instead of explain

Where a comment exists only because an identifier is unclear, rename the identifier and delete the
comment. A rename is the only non-comment edit any child ticket may make, and it must be a pure
rename — same members, same call sites, tests green.

## 3. Model impact

| Context | What it gains |
|---|---|
| Reference Library | nothing behavioural; `ReferenceLibrary`, `IDocumentTree`, `LibraryReference` read at their code length |
| Session Setup | nothing behavioural |
| Session Execution | nothing behavioural; `DrawingSession<TImage>` drops ~200 comment lines |
| Preferences | nothing behavioural |

**Object catalogue.** Stays at ten. No type is added, removed, renamed as a type, or moved between
files.

**Invariants**

| Id | Statement | New / changed |
|---|---|---|
| — | none | none |

The only invariant traffic runs the other way: where a comment states a rule DOMAIN-MODEL.md does
not, that PR adds the rule to DOMAIN-MODEL.md — as a new `INV-<FAM>-<n>` if it is an invariant, or
as a card sentence if it is not. Such an addition documents behaviour that already ships; it is
never a change to it.

**Contracts crossed** — none. No `SessionConfig` field, `Extra*` constant, `Settings` property or
pool shape is touched.

## 4. Stories

Each story is one domain object (or one file cluster owned by one), and one pull request.

### 4.1 The library reads at its code length → FD-017

`ReferenceLibrary.cs` (83 comment lines / 250) — objects 2 and 3, `ReferenceLibrary` and
`IDocumentTree`/`DocumentEntry`, which share the file.

### 4.2 The remembered folder reads at its code length → FD-018

`LibraryReference.cs` (73 / 208) — object 4, `LibraryReference`/`PersistedGrant`.

### 4.3 Setup reads at its code length → FD-019

`SessionSetup.cs` (39 / 83) — objects 5 and 6, `SessionSetup` and `SessionConfig`, which share the
file.

### 4.4 The session aggregate reads at its code length → FD-020

`Session/DrawingSession.cs` (218 / 744) — object 7. The largest single win and the one with the
most rules to check against DOMAIN-MODEL.md before deleting.

### 4.5 The viewer tools read at their code length → FD-021

`Session/ViewerTools.cs` (21 / 58) — object 8.

### 4.6 Preferences read at their code length → FD-022

`Data/Settings.cs` (63 / 238) — object 9.

### 4.7 The rendering services read at their code length → FD-023

`BitmapMath.cs` (13 / 45), `GridContrast.cs` (53 / 201), `PoseImage.cs` (15 / 38),
`ImageDecoding.cs` (12 / 45) — supporting rendering services, ARCHITECTURE.md §16, not catalogue
objects.

### 4.8 Library loading reads at its code length → FD-024

`LibraryLoader.cs` (97 / 282), `LibraryLoadState.cs` (17 / 25), `LoadGeneration.cs` (20 / 32) — the
FD-009 background-load machinery.

### 4.9 The folder screen reads at its code length → FD-025

`MainActivity.cs` (287 / 1047).

### 4.10 The player screen reads at its code length → FD-026

`SessionActivity.cs` (305 / 1210).

### 4.11 The suites keep their why and lose their narration → FD-027

`FigureDrawing.Tests` (1,111) and `FigureDrawing.UITests` (317). Narrow: the CLAUDE.md why-comment
above each test stays. Only arrange/act/assert narration inside bodies goes.

### 4.12 The diet holds → FD-028

A contract test capping comment density, so the prose does not grow back. Last, and only if the
eleven before it landed.

## 5. Layer split

| Story | Core | Android | Test tier |
|---|---|---|---|
| 4.1–4.7 | comments only | — | existing unit suite must stay green |
| 4.8 | `LibraryLoadState`, `LoadGeneration` | `LibraryLoader` | existing unit + contract suites |
| 4.9–4.10 | — | `MainActivity`, `SessionActivity` | existing contract suites |
| 4.11 | — | — | the suites themselves |
| 4.12 | — | — | new contract test |

Stories 4.9 and 4.10 have an empty Core column because the files are Activities: the rule is not
that comment removal needs the platform, it is that these two files only exist on the platform
side.

## 6. UI and design

- Nocturne tokens and component styles reused: none touched.
- New styles needed: none.
- New strings: none.
- Foldable / configuration change behaviour: unchanged.
- Minimum API 26 holds: yes.

## 7. Acceptance signals

| Signal | Target |
|---|---|
| Unit tests | `./nx.bat run FigureDrawing.Tests:test` green after every child, unchanged test list |
| Behaviour | comment-stripped source is identical before and after, except §2.3 renames |
| Density | every touched production file at or under 5% comment lines, no comment block over 2 lines |
| Knowledge | every deleted rule is findable in DOMAIN-MODEL.md or ARCHITECTURE.md afterwards |
| On-device | not required — no child changes behaviour |

No analytics. Nothing here depends on data the app does not already have on device.

## 8. Scope

**In scope**

- Comment removal in the nine implemented catalogue objects and the files that host them → FD-017…FD-022
- Comment removal in the rendering services and the load machinery → FD-023, FD-024
- Comment removal in the two Activities → FD-025, FD-026
- Test-body narration in the two test projects → FD-027
- A density guard → FD-028

**Out of scope**

- The CLAUDE.md rule that every test carries a why-comment with its invariant id. It stands; FD-027
  is written around it.
- Prose in `docs/`. This ticket moves rules *into* those files and never trims them.
- `.axml` layouts, `strings.xml`, `styles.xml` and build files.
- Behaviour, formatting, and reordering of members. A comment PR that also reflows code cannot be
  reviewed as a comment PR.
- `.claude/worktrees/` — a stale copy of the tree, not the tree.

## 9. Risks

| Risk | Mitigation |
|---|---|
| A rule that exists only as a comment is deleted with it | §2.2 keep-list plus the §2 migration rule: confirm in the docs, or add it there, before deleting |
| A "comment-only" PR quietly changes behaviour | comment-stripped diff must be empty; the fast suite runs on every child |
| Contract tests break | they cannot: `SourceContract` strips comments and literals before asserting (ARCHITECTURE.md §11), so a comment can neither satisfy nor break one |
| Twelve PRs drift apart on what "almost all" means | §2 is the single definition; children cite it rather than restating it |
| Merge conflicts between children | children touch disjoint files; land them in the §11 order |

## 10. Open questions

| Question | Owner |
|---|---|
| Does the type-level one-line header in §2.2.5 survive at all, or does every type go bare? | user — FD-017 sets the precedent the rest follow |
| Is FD-028's density cap worth a contract test, or is review enough? | user, after FD-026 |

## 11. Sequencing

FD-017 → FD-018 → FD-019 → FD-021 → FD-022 → FD-023 → FD-024 → FD-020 → FD-025 → FD-026 → FD-027 →
FD-028.

FD-017 goes first because it is the smallest file with a real design essay in it, so it settles the
§2.2.5 question cheaply. The small Core objects follow. `DrawingSession` (FD-020) waits until the
keep-list has been exercised six times, because it holds the most rules that may exist only as
comments. The Activities follow the Core they wire. FD-027 is last of the removals because the test
suites are the safety net for the eleven before it, and FD-028 only makes sense once there is a
diet to hold.

Each child leaves the app shipping exactly what it shipped before; what each one leaves behind is
one file a reader can hold in their head.
