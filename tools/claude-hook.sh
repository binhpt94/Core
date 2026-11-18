#!/usr/bin/env bash
# Claude Code hooks (configured in .claude/settings.json). Reads the hook JSON on stdin.
#   session        → SessionStart: prints task / last gate / chain status (becomes the agent's context)
#   pre            → PreToolUse: scope guard blocks out-of-scope writes BEFORE they happen; snapshot before Bash
#   post           → PostToolUse: records the tool call; files changed (Edit or Bash, via git snapshot) + per-file rules
#   subagent-stop  → SubagentStop: records the subagent's end
#   stop           → Stop: reconcile, scope, test-weakening guard, then the gate; exit 2 = keep working (max 3 blocks)
# Every hook appends to .agent/events.jsonl (hash-chained). Dashboard: ./tools/test-gate.sh dash | serve
# Silent and ~0.1 s when everything is fine, so it costs no tokens.
set -euo pipefail
exec "$(cd "$(dirname "$0")" && pwd)/test-gate.sh" "hook-$1"
