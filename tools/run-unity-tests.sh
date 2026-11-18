#!/usr/bin/env bash
# Runs Unity tests in batchmode. Usage: ./tools/run-unity-tests.sh [EditMode|PlayMode] [testCategory]
#   ./tools/run-unity-tests.sh EditMode "View:MathQuiz.QuizView"   # layout gate of one view (L2 + L3)
#   LAYOUT_WRITE=1 ./tools/run-unity-tests.sh EditMode "View:MathQuiz.QuizView"   # OWNERS: (re)write its layout baseline
# Set UNITY_PATH to the editor binary, e.g.
#   macOS:   /Applications/Unity/Hub/Editor/2022.3.50f1/Unity.app/Contents/MacOS/Unity
#   Windows: "C:/Program Files/Unity/Hub/Editor/2022.3.50f1/Editor/Unity.exe"   (Git Bash)
#   Linux:   ~/Unity/Hub/Editor/2022.3.50f1/Editor/Unity
set -euo pipefail
PLATFORM="${1:-EditMode}"
CATEGORY="${2:-}"
PROJECT="$(cd "$(dirname "$0")/.." && pwd)"
: "${UNITY_PATH:?Set UNITY_PATH to your Unity editor executable}"
mkdir -p "$PROJECT/TestResults"
RESULTS="$PROJECT/TestResults/$PLATFORM.xml"

set +e
"$UNITY_PATH" -batchmode -nographics -projectPath "$PROJECT" \
  -runTests -testPlatform "$PLATFORM" -testResults "$RESULTS" \
  ${CATEGORY:+-testCategory "$CATEGORY"} \
  -logFile "$PROJECT/TestResults/$PLATFORM.log"
CODE=$?
set -e

echo "Results: $RESULTS (exit code $CODE; 0 = passed, 2 = test failures, other = Unity error, see log)"
exit $CODE
