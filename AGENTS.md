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
testing strategy, and the anti-patterns that count as violations.

Short version: all logic that can be written without Android goes in `FigureDrawing.Core` and is
unit tested there; Activities only wire Core to views.
