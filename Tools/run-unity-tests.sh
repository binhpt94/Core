#!/usr/bin/env bash
# Runs Unity tests in batchmode. Usage: ./Tools/run-unity-tests.sh [EditMode|PlayMode] [testCategory]
#   ./Tools/run-unity-tests.sh EditMode "View:MathQuiz.QuizView"   # layout gate of one view (L2 + L3)
#   LAYOUT_WRITE=1 ./Tools/run-unity-tests.sh EditMode "View:MathQuiz.QuizView"   # OWNERS: (re)write its layout baseline
# Set UNITY_PATH to the editor binary (if unset: the Unity Hub install of ProjectVersion.txt's version is used), e.g.
#   macOS:   /Applications/Unity/Hub/Editor/2022.3.50f1/Unity.app/Contents/MacOS/Unity
#   Windows: "C:/Program Files/Unity/Hub/Editor/2022.3.50f1/Editor/Unity.exe"   (Git Bash)
#   Linux:   ~/Unity/Hub/Editor/2022.3.50f1/Editor/Unity
set -euo pipefail
PLATFORM="${1:-EditMode}"
CATEGORY="${2:-}"
PROJECT="$(cd "$(dirname "$0")/.." && pwd)"
if [ -z "${UNITY_PATH:-}" ]; then
  # Not set: use the Unity Hub install of the project's own editor version (ProjectSettings/ProjectVersion.txt).
  VERSION="$(sed -n 's/^m_EditorVersion: *//p' "$PROJECT/UnityProject/ProjectSettings/ProjectVersion.txt" | tr -d '\r')"
  for candidate in "$HOME/Unity/Hub/Editor/$VERSION/Editor/Unity" \
                   "/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity" \
                   "/c/Program Files/Unity/Hub/Editor/$VERSION/Editor/Unity.exe"; do
    if [ -x "$candidate" ]; then UNITY_PATH="$candidate"; break; fi
  done
fi
: "${UNITY_PATH:?Set UNITY_PATH to your Unity editor executable (none found for the project version in Unity Hub)}"
mkdir -p "$PROJECT/Dashboard/Data"
RESULTS="$PROJECT/Dashboard/Data/$PLATFORM.xml"

set +e
"$UNITY_PATH" -batchmode -nographics -projectPath "$PROJECT/UnityProject" \
  -runTests -testPlatform "$PLATFORM" -testResults "$RESULTS" \
  ${CATEGORY:+-testCategory "$CATEGORY"} \
  -logFile "$PROJECT/Dashboard/Data/$PLATFORM.log"
CODE=$?
set -e

echo "Results: $RESULTS (exit code $CODE; 0 = passed, 2 = test failures, other = Unity error, see log)"
exit $CODE
