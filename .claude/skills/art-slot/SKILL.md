---
name: art-slot
description: Make or iterate the art of a game art slot with this repo's standard pipeline - Claude draws the candidate as SVG from the generated brief, adds it as a new version, Unity binds it, tests and the compare page show it, the owner's one-line notes drive the next version; Codex CLI paints the final only on the owner's request. Use when asked to draw, redo, fix, version, compare or finalize art for a slot (e.g. MathQuiz.Slime), or to "make art"/"vẽ art"/"làm art" for a feature.
---

# Art slot: the standard loop

Read `Docs/PLANNING.md` → "Standard art process" once. The tools do the bookkeeping; you draw and report.

## 0. Before drawing
- Task scope must include the slot's feature (`./Tools/test-gate.sh task show`).
- `./Tools/test-gate.sh art brief <SLOT> --print` → read the brief AND the prompt: size range (delivery px = 2× units),
  pivot, intent, style lock, the **Avoid** list (earlier rejections: never repeat one).
- `./Tools/test-gate.sh art versions <SLOT>` → what exists, what each tier uses, what is approved.
- Never read `Dashboard/Data/` (it is the owner's); everything you need comes from `--print` and command output.

## 1. Draw the candidate (SVG, by hand)
- Write `.scratch/art/<SLOT>.svg` (scratch is ignored). `width`/`height`/`viewBox` = the delivery size you aim for
  (the brief's "Gợi ý vẽ quanh W×H px"), transparent background, the subject **tight to the edges** (no empty margin:
  the PNG size becomes the in-game size), the pivot point where the contract says (50/50 = centre of the canvas;
  50/0 = bottom centre: feet on the bottom edge).
- Readability first: it shows ~1/20 of the screen height. Bold silhouette, 2-4 flat colours + one shade, thick dark
  outline, big simple features. No text, no thin lines under ~3 px, no tiny details. Follow the style lock exactly.
- Variation for a redo: change what the owner's note names, keep the rest (parent = the version they commented on).

## 2. Add it as a version
```bash
./Tools/test-gate.sh art add <SLOT> .scratch/art/<SLOT>.svg --tier candidate --by claude --parent vNNN --note "<what changed, 1 line>"
```
Inkscape renders the PNG; the new version is used as the candidate. A render error = fix the SVG, never hand-make a PNG.

## 3. Bind and check in Unity
- Unity must import it: MCP `refresh_unity` (allowed) or the owner focuses the Editor; `ArtImport` builds
  `<Name>.candidate.prefab` + `<Name>.art.asset`. If the Editor is closed, batchmode tests import it too.
- Run EditMode `Art:<SLOT>` (contract). Pictures: `./Tools/test-gate.sh art shots` (Editor closed; Unity batchmode with a GPU,
  regenerates every page) or, with the Editor open, ask the owner to click *Tools > Game > Art > Snapshot*.
- `A-SIZE`/`A-PIVOT` failing = the SVG canvas is wrong (size or where the pivot falls): fix it in a new version.
- `./Tools/test-gate.sh art compare <SLOT>` → tell the owner to open `Dashboard/Data/art/briefs/<SLOT>.compare.html`.

## 4. Owner feedback → next version
- The owner writes a note per picture on the page (it becomes `art note`) or tells you; the note lands in the slot's
  style file and every later prompt. Redraw from step 1 with `--parent` = the version they judged.
- To compare or roll back: `art use <SLOT> vNNN` (never delete versions).

## 5. Final (Codex) - ONLY when the owner asks
- It costs money and minutes. Show the plan first: `./Tools/test-gate.sh art codex <SLOT> --from vNNN --dry-run`.
- Then `art codex <SLOT> --from vNNN` (run in background; it adds a `final` version by codex). Same checks, same page.
- Approval is the owner's: `./Tools/test-gate.sh art accept <SLOT>` is an owner command (run it alone only when asked;
  they answer yes/no).

## Report
Version id, what changed, test results (`Art:<SLOT>`), whether pictures were taken (`art shots`), the compare page path, anything pending (Unity not run,
layout baseline waiting for the owner). Never claim it "looks good": that is the owner's call.
