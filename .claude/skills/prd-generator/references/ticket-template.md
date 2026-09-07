# <Title in the repo's vocabulary>

<!-- This H1 becomes the issue title and is not part of the body. -->

**Story:** _<one sentence from the artist's point of view, in §15 terms>_
**Depends on:** #N, #M (or `none`)

## Summary

Two or three sentences. What the artist can do after this that they cannot do now, and the one
rule that makes it true.

## Model placement

| | |
|---|---|
| Context | <Reference Library / Session Setup / Session Execution / Preferences / Rendering> |
| Owning object | `<Core type>` |
| New Core type | no — <or the justification against DOMAIN-MODEL.md §9> |
| New invariants | `INV-<FAM>-<n>` — <one line each> |
| Invariants changed | `INV-<FAM>-<n>` — <what changes; flag if breaking> or `none` |
| Crosses a boundary | <contract: `SessionConfig` field / pool / intent extra / `Settings` property> or `no` |

## Approach

- **Core:** `<Type>.<Method>` — <the rule, stated as behaviour, not as code>.
- **Android:** <which Activity, which views, what it renders and forwards>.
- **Resources:** <new view ids, new strings, Nocturne styles reused> or `none`.
- **Persistence:** <new `Settings` property + default> or `none`.
- **Injected dependency:** <clock / `Random` / loader / `IDocumentTree`> or `none`.

## Acceptance criteria

- [ ] <observable behaviour>
- [ ] <edge case: empty pool / unreadable image / already complete / paused / backgrounded>
- [ ] <negative: the invariant this must not break>
- [ ] <counting effect: `CompletedCount` / `SkippedCount` / `TotalDrawingTime`, including "unchanged">

## Tests

| Tier | What |
|---|---|
| Unit (`FigureDrawing.Tests`) | <file + the invariants it covers> |
| Contract | <`UiResourceContractTests` / `SessionScreenContractTests` entries> or `n/a` |
| E2E-model | <`SessionE2ETests` flow touched> or `n/a` |
| UI (Appium) | `n/a` — <or why Core cannot reach it> |

## Out of scope

- <explicitly excluded>
- <deferred to a follow-up issue>

## Risks

- <memory / intent size / main-thread decode / lifecycle / SAF grant / foldable, where relevant>

## Open questions

- <question> — decided by <user / implementation>
