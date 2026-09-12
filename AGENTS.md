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
  elsewhere. Example: `./nx.bat run DongoDraw.Tests:test`.
- Do NOT use `pnpm nx`, `npm exec nx`, `npx nx` or `yarn nx`. pnpm is not installed, and the npm/npx
  forms resolve a globally installed Nx against this workspace's vendored version and die with
  `ERR_UNSUPPORTED_ESM_URL_SCHEME`.
- There is no `node_modules/@nx/<plugin>/PLUGIN.md` to read here.
- The one command that bypasses Nx is the emulator run:
  `dotnet build DongoDraw.csproj -t:RunEmulator` — see
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) §12.


# Architecture

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing code. It defines the Core/Android
split, the rules for crossing that boundary, threading and lifecycle requirements, the four-tier
testing strategy, and the anti-patterns that count as violations.

Short version: all logic that can be written without Android goes in `DongoDraw.Core` and is
unit tested there; Activities only wire Core to views.


# Implementation Workflow

Every feature or bug fix follows this sequence. No step is optional.

## 1. TDD — Red, Green, Refactor

Use the `tdd` skill (`/tdd`) to drive implementation:

1. **Red.** Write failing tests first. Run them (`./nx.bat run DongoDraw.Tests:test`) and
   confirm they fail. A test that has never been red has not been shown to test anything.
2. **Green.** Write the minimal code to make the tests pass. Run the tests again and confirm green.
3. **Refactor.** Clean up while green. Tests must stay green after every change.

## 2. Review — Fix — Repeat

After tests are green, run the `review-crew` skill (`/review-crew`) for a full multi-agent review.

1. **Review.** Run `/review-crew`. Read every finding.
2. **Fix.** Address all findings that are not marked "Out of scope". Re-run tests after each fix to
   stay green.
3. **Re-review.** Run `/review-crew` again on the updated code.
4. **Repeat** steps 2–3 until the review returns zero non-out-of-scope findings.

Only then is the change complete. A review that still has actionable findings means the work is not
done — do not commit, do not move on.
