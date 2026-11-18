#!/usr/bin/env bash
# Builds Template/: a clean Core (rules, gate, hooks, dashboard, docs, Core/, an empty Bootstrap) WITHOUT this game's
# features, to start a new game from. Template/ is generated and gitignored: never edit it, change Tools/TemplateKit/ and re-run.
#   ./Tools/make-template.sh             write Template/ and verify it (graph --write, static rules, dotnet tests)
#   ./Tools/make-template.sh --no-verify write only
# New game: copy Template/ to a new folder, `git init`, open UnityProject/ in Unity Hub, `./Tools/test-gate.sh new-feature Name`.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/Template"
KIT="$ROOT/Tools/TemplateKit"
G="UnityProject/Assets/_Game"
cd "$ROOT"

# 1. Every tracked file (as it is in the working tree), minus the game: game features (Ads stays: the SDK-adapter example), Bootstrap tests,
#    generated docs and the plan. Folder .meta files of removed folders go too.
rm -rf "$OUT"
mkdir -p "$OUT"
git ls-files -z | grep -zv -E "^$G/Features/(MathQuiz|Progress)(/|.meta$)|^$G/Bootstrap/Tests|^Docs/Architecture/|^Docs/Plan/|^Tools/TemplateKit/|^Tools/make-template\.sh$" \
  | tar --null -T - -cf - | tar -xf - -C "$OUT"
mkdir -p "$OUT/Docs/Plan" "$OUT/Tools/TemplateKit"

# 2. The game-free versions of the few files that wire the game.
cp "$KIT/GameCompositionRoot.cs.template"        "$OUT/$G/Bootstrap/GameCompositionRoot.cs"
cp "$KIT/Game.Bootstrap.asmdef.template"         "$OUT/$G/Bootstrap/Game.Bootstrap.asmdef"
cp "$KIT/Game.Bootstrap.Editor.asmdef.template"  "$OUT/$G/Bootstrap/Editor/Game.Bootstrap.Editor.asmdef"
cp "$KIT/GameMenu.cs.template"                   "$OUT/$G/Bootstrap/Editor/GameMenu.cs"
cp "$KIT/features.md.template"                   "$OUT/Docs/Plan/features.md"
cp "$KIT/README.md.template"                     "$OUT/README.md"
cp -r "$KIT/." "$OUT/Tools/TemplateKit/"
cp "$ROOT/Tools/make-template.sh" "$OUT/Tools/make-template.sh"

echo "Template/ written ($(cd "$OUT" && find . -type f | wc -l | tr -d ' ') files)."
[ "${1:-}" = "--no-verify" ] && exit 0

# 3. Verify the template on its own: regenerate its system map, then the same gates a new project starts with.
cd "$OUT"
./Tools/test-gate.sh graph --write >/dev/null
./Tools/test-gate.sh check --all
dotnet test Tools/LogicTests --nologo -v q
echo "Template OK: $OUT (Unity compile not checked here: open Template/UnityProject in Unity once)."
