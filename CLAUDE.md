## Running Nx in THIS workspace

Overrides the generic guidance above. This workspace has no `package.json`, no lockfile and no
root `node_modules` — Nx is vendored under `.nx/installation/` and driven by a wrapper script.

- Run every target as `./nx.bat run <project>:<target>` on Windows, `./nx run <project>:<target>`
  elsewhere. Example: `./nx.bat run FigureDrawing.Tests:test`.
- Do NOT use `pnpm nx`, `npm exec nx`, `npx nx` or `yarn nx`. pnpm is not installed, and the npm/npx
  forms resolve a globally installed Nx against this workspace's vendored version and die with
  `ERR_UNSUPPORTED_ESM_URL_SCHEME`.
- The one command that bypasses Nx is the emulator run:
  `dotnet build FigureDrawing.csproj -t:RunEmulator` — see
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) §12.

# Architecture

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing code. It defines the Core/Android
split, the rules for crossing that boundary, threading and lifecycle requirements, the four-tier
testing strategy (unit, contract, E2E-model, UI), and the anti-patterns that count as violations.

Short version: all logic that can be written without Android goes in `FigureDrawing.Core` and is
unit tested there; Activities only wire Core to views.

## Comments

Production source is roughly 4% comment lines and is meant to stay there (#11 removed about 1,900).
Before adding one, the question is not "is this helpful?" but "does this belong in the code at all?".

**Delete, or never write:**

- A restatement of the code beside it, or of a signature above it
- A section-divider banner (`// --- Rendering ---`)
- A design history, a placement essay, or an argument for where a type lives — that is
  `docs/DDD-ARCHITECTURE.md` §16 and §20
- Any rule already carried by `docs/DOMAIN-MODEL.md` under an invariant id, or by
  `docs/ARCHITECTURE.md` under a section. **Check before writing** — those two documents are far more
  complete than they look, and §7 and §8 in particular already state most of what an Activity is
  tempted to explain
- Per-member prose on an enum or a record's fields

**Keep, in two lines or fewer:**

1. A non-obvious *why* that a plausible, well-meaning edit would silently re-break, that lives
   nowhere else, citing its invariant id where it has one
2. A platform trap a reader cannot infer — SAF grant flags, JNI peer disposal, an API-level guard
3. A `#pragma warning disable` justification
4. Above a test, the why-comment the testing conventions below require, with its invariant id
5. One line of type-level header, only where the type's role is not readable from its name and
   namespace

**If a rule is real and documented nowhere, add it to the doc rather than to a comment.** That is
the migration rule: the doc gets the rule, then the comment goes.

**Budget.** As a review guide, not a gate: a production file over `max(5% of its lines, 2 lines)` of
comment lines, or carrying a comment block longer than two lines, is worth a second look. It is a
smell rather than a rule — a file can have a good reason to exceed it, and a file under it can still
be full of noise. Measured, not guessed: the two smallest files sit at 1 and 2 lines because 5% of a
13-line file is zero, which is why the floor exists.

This was specified as an automated test (#23) and deliberately rejected. Two reasons, both worth
knowing: a comment-density test cannot pass its own rule, since every test file needs a why-comment
above every test; and volume is not the defect that matters. Nineteen comment defects found in review
of #20 and #21 were all *rewrites* that inverted a fact or dropped a pronoun's antecedent — correct in
length, wrong in content. `SourceContract` blanks comments before the contract tier reads anything,
so no test here can see comment content. **Comment correctness is a review responsibility.**

When compressing an existing comment rather than deleting it, re-read it against the code
afterwards. That step is where those nineteen defects came from.

## Testing Conventions

[ARCHITECTURE.md §11](docs/ARCHITECTURE.md#11-testing-strategy) is authoritative: which tier a test
belongs in, one file per Core type or invariant family, and what a contract test may assert. The
rules below are the workflow around it, not a second policy.

### TDD Workflow
- Write the failing test BEFORE the implementation, and run it to see it fail — a test that has
  never been red has not been shown to test anything
- Name tests `Subject_Behaviour` in PascalCase, describing the behaviour and not the method:
  `PickedFolder_IsRestoredOnRelaunch`, `AWriteOnlyGrant_DoesNotRestoreTheLibrary`
- Assert one behaviour per test. Several `Assert`s that pin one behaviour are one test; two
  behaviours are two tests
- A comment above the test says *why the rule exists*, citing the invariant id where there is one

### Test-First Rules
- When I ask for a feature, write tests first
- Tests should FAIL initially (no implementation exists)
- Only after tests are written, implement minimal code to pass
- Run the fast tier with `./nx.bat run FigureDrawing.Tests:test`; the Appium tier is opt-in and
  needs an emulator (`scripts/run-appium-tests.ps1`)

## Agent skills

### Issue tracker

GitHub Issues on `JeffreyMcKinley/FigureDrawing`, driven with the `gh` CLI. The issue number is the
ticket id — cite work as `#14`. The old `docs/prds/FD-0NN-*.md` files were migrated into issues and
deleted. See [`docs/agents/issue-tracker.md`](docs/agents/issue-tracker.md).

### Triage labels

The five canonical roles as real GitHub labels, plus an orthogonal `context:*` label per bounded
context. Shipped work is a closed issue, not a label. See
[`docs/agents/triage-labels.md`](docs/agents/triage-labels.md).

### Domain docs

Single-context. The glossary is `docs/DDD-ARCHITECTURE.md` §15/§16; object rules and invariant ids are
in `docs/DOMAIN-MODEL.md`. See [`docs/agents/domain.md`](docs/agents/domain.md).