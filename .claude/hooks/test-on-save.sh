#!/bin/bash
# Runs the unit + contract suite after a C# file is written.
#
# This workspace has no package.json, no lockfile and no node_modules — Nx is vendored under
# .nx/installation and driven by the ./nx wrapper (CLAUDE.md). `npm test` does not exist here.
#
# Reads the hook payload on stdin (PostToolUse gives {"tool_input":{"file_path":...}}) and falls
# back to $1 so the script is still runnable by hand.
set -uo pipefail

payload=""
if [ ! -t 0 ]; then
  payload=$(cat)
fi

file="${1:-}"
if [ -z "$file" ] && [ -n "$payload" ]; then
  file=$(printf '%s' "$payload" | sed -n 's/.*"file_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')
fi

case "$file" in
  *.cs) ;;
  *) exit 0 ;;
esac

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
cd "$root" || exit 0

# The UI tests need an emulator and are opt-in (RUN_APPIUM=1), so this is the fast tier only.
#
# Bounded: the suite itself runs in ~3s, but an Appium run or an Android build in another terminal
# holds the msbuild/NuGet locks this needs, and a save should never block a session on that.
runner=./nx
[ -x "$runner" ] || runner=./nx.bat

if command -v timeout >/dev/null 2>&1; then
  timeout 180 "$runner" run FigureDrawing.Tests:test 2>&1 | tail -20
  [ "${PIPESTATUS[0]}" -eq 124 ] && echo "test-on-save: timed out after 180s (another build holding locks?); skipped."
else
  "$runner" run FigureDrawing.Tests:test 2>&1 | tail -20
fi

exit 0
