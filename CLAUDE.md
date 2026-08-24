<!-- nx configuration start-->
<!-- Leave the start & end comments to automatically receive updates. -->

# General Guidelines for working with Nx

- For navigating/exploring the workspace, invoke the `nx-workspace` skill first - it has patterns for querying projects, targets, and dependencies
- When running tasks (for example build, lint, test, e2e, etc.), always prefer running the task through `nx` (i.e. `nx run`, `nx run-many`, `nx affected`) instead of using the underlying tooling directly
- Prefix nx commands with the workspace's package manager (e.g., `pnpm nx build`, `npm exec nx test`) - avoids using globally installed CLI
- You have access to the Nx MCP server and its tools, use them to help the user
- For Nx plugin best practices, check `node_modules/@nx/<plugin>/PLUGIN.md`. Not all plugins have this file - proceed without it if unavailable.
- NEVER guess CLI flags - always check nx_docs or `--help` first when unsure

## Scaffolding & Generators

- For scaffolding tasks (creating apps, libs, project structure, setup), ALWAYS invoke the `nx-generate` skill FIRST before exploring or calling MCP tools

## When to use nx_docs

- USE for: advanced config options, unfamiliar flags, migration guides, plugin configuration, edge cases
- DON'T USE for: basic generator syntax (`nx g @nx/react:app`), standard commands, things you already know
- The `nx-generate` skill handles generator discovery internally - don't call nx_docs just to look up generator syntax


<!-- nx configuration end-->

## Running Nx in THIS workspace

Overrides the generic guidance above. This workspace has no `package.json`, no lockfile and no
root `node_modules` — Nx is vendored under `.nx/installation/` and driven by a wrapper script.

- Run every target as `./nx.bat run <project>:<target>` on Windows, `./nx run <project>:<target>`
  elsewhere. Example: `./nx.bat run FigureDrawing.Tests:test`.
- Do NOT use `pnpm nx`, `npm exec nx`, `npx nx` or `yarn nx`. pnpm is not installed, and the npm/npx
  forms resolve a globally installed Nx against this workspace's vendored version and die with
  `ERR_UNSUPPORTED_ESM_URL_SCHEME`.
- There is no `node_modules/@nx/<plugin>/PLUGIN.md` to read here.
- The one command that bypasses Nx is the emulator run:
  `dotnet build FigureDrawing.csproj -t:RunEmulator` — see
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) §12.

# Architecture

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing code. It defines the Core/Android
split, the rules for crossing that boundary, threading and lifecycle requirements, the four-tier
testing strategy (unit, contract, E2E-model, UI), and the anti-patterns that count as violations.

Short version: all logic that can be written without Android goes in `FigureDrawing.Core` and is
unit tested there; Activities only wire Core to views.

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

Issues are markdown files: `docs/prds/FD-0NN-<slug>.md`, indexed in `docs/prds/README.md`. The `gh`
CLI is installed and `origin` is a GitHub remote, but issues still live in `docs/prds/` — do not
open GitHub Issues. See [`docs/agents/issue-tracker.md`](docs/agents/issue-tracker.md).

### Triage labels

The five canonical roles plus a local `shipped`, carried on a `Status:` line in each ticket. See
[`docs/agents/triage-labels.md`](docs/agents/triage-labels.md).

### Domain docs

Single-context. The glossary is `docs/ARCHITECTURE.md` §15/§16; object rules and invariant ids are
in `docs/DOMAIN-MODEL.md`. See [`docs/agents/domain.md`](docs/agents/domain.md).