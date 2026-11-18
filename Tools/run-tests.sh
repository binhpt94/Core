#!/usr/bin/env bash
# Fast pure-C# test loop (no Unity needed). Extra args go to `dotnet test`, e.g.:
#   ./tools/run-tests.sh --filter FullyQualifiedName~Game.MathQuiz
set -euo pipefail
cd "$(dirname "$0")/.."
dotnet test LogicTests --nologo "$@"
