# <Feature name>

<!-- This H1 becomes the tracking issue's title and is not part of the body. -->

> Multi-ticket feature. Child tickets carry the acceptance criteria; this document carries the
> shape. Use [ticket-template.md](ticket-template.md) for each child.

**Child tickets:** #N, #N+1, … (filled in once the children are filed; the state is the issue's
triage label, not a line here)

## 1. Problem

What the artist cannot do today, and why that matters mid-session. One or two paragraphs. No
market framing — there is one user and no competitor.

## 2. Proposed shape

The feature in three or four sentences, in §15 vocabulary. What changes on screen, what changes
in what the session counts, what persists.

## 3. Model impact

| Context | What it gains |
|---|---|
| Reference Library | |
| Session Setup | |
| Session Execution | |
| Preferences | |

**Object catalogue.** Does this stay at ten objects? If it adds one, justify against
[DOMAIN-MODEL.md §9](../../../../docs/DOMAIN-MODEL.md) — what rule has no existing owner, and why
absorbing it into a neighbour is worse.

**Invariants**

| Id | Statement | New / changed |
|---|---|---|
| `INV-<FAM>-<n>` | | |

**Contracts crossed** — new `SessionConfig` fields, new `Extra*` constants, new `Settings`
properties, changes to the pool's shape. Each one is a change at both ends in a single commit.

## 4. Stories

Numbered, each mapping to one child ticket.

### 4.1 <Story name> → #N

```
As an artist,
I want to <action>,
So that <benefit>.
```

- [ ] <acceptance criterion>
- [ ] <edge case>
- [ ] <negative>

## 5. Layer split

| Story | Core | Android | Test tier |
|---|---|---|---|
| 4.1 | `<Type>.<Method>` | `<Activity>` — <wiring> | unit |

Anything with an empty Core column needs a sentence explaining why the rule is not expressible
without the platform.

## 6. UI and design

- Nocturne tokens and component styles reused: <`.tool-chip`, `.btn-*`, `.card`, `.input`, tab bar>
- New styles needed: <or `none` — retune in `styles.xml`, never inline>
- New strings: `Resources/values/strings.xml` + `UiResourceContractTests`
- Foldable / configuration change behaviour: <what re-lays out in place>
- Minimum API 26 holds: <yes / what raises it>

## 7. Acceptance signals

| Signal | Target |
|---|---|
| Unit tests | every invariant in §3 has a named test |
| Suite | `./nx.bat run FigureDrawing.Tests:test` green |
| On-device | <observable behaviour on the emulator> |
| Budget | <memory / frame / decode bound> |

No analytics. Nothing here may depend on data the app does not already have on device.

## 8. Scope

**In scope**

- <capability> → #N

**Out of scope**

- <explicitly excluded, and why>
- <deferred, with the ticket it would become>

## 9. Risks

| Risk | Mitigation |
|---|---|
| | |

Check against the known costs in [ARCHITECTURE.md §20](../../../../docs/ARCHITECTURE.md): intent
extra size, main-thread decode, unrecycled bitmaps, process death, SAF grant expiry,
`MainActivity` spanning three contexts.

## 10. Open questions

| Question | Owner |
|---|---|
| | |

## 11. Sequencing

#N → #N+1 → …, and what each one leaves the app able to do on its own.
