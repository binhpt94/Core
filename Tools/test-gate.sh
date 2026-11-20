#!/usr/bin/env bash
# Incremental test gate + architecture checks (Tools/TestGate/Program.cs).
#   ./Tools/test-gate.sh plan              # what would run and why (no tests executed)
#   ./Tools/test-gate.sh run               # G0 static rules → G1 dotnet tests → Unity G2/G3 only if affected
#   ./Tools/test-gate.sh run --full        # everything (pre-merge / nightly / release)
#   ./Tools/test-gate.sh run --base main   # compare against another branch (default: origin/main, else HEAD)
#   ./Tools/test-gate.sh check [--all|--staged] [paths...]   # static rules only, milliseconds
#   ./Tools/test-gate.sh refs Assets/_Game/Features/X  # incoming references (run before deleting X)
#   ./Tools/test-gate.sh deadcode [--all] [paths...]   # ARCH033: code nothing references (changed files by default)
#   ./Tools/test-gate.sh task start "Retire R-X" --retire R-X   # remove a [dropped] requirement's tests, code, mentions
#   ./Tools/test-gate.sh new-view MathQuiz ResultPopup   # view/popup testable in isolation (ARCH100/101 ready)
#   ./Tools/test-gate.sh reset             # clear the Unity retry budget after a real fix
# Unity gates run only when UNITY_PATH points to the editor executable.
# Exit codes: 0 pass (or Unity pending), 1 rules/pure tests failed, 2 Unity failed, 4 retry budget exhausted.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DLL="$ROOT/Tools/TestGate/bin/Release/net8.0/TestGate.dll"
# Prebuilt runner: ~0.1 s per call instead of `dotnet run`. Rebuild only when the gate or rules changed.
if [ ! -f "$DLL" ] || [ -n "$(find "$ROOT/Tools/TestGate" "$ROOT/UnityProject/Assets/_Game/Architecture/Rules" \( -name '*.cs' -o -name '*.html' \) -newer "$DLL" -print -quit 2>/dev/null)" ]; then
  dotnet build "$ROOT/Tools/TestGate" -c Release -v q --nologo >/dev/null
fi
cd "$ROOT"
exec dotnet "$DLL" "$@"
