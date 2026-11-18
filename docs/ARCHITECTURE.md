# Architecture

**Feature modules + Clean Architecture Lite + manual DI + asmdef boundaries**, made safe for AI agents by
**enforcement instead of instructions**: every important rule is checked by a tool that runs automatically
(compiler, hooks, tests, Unity gates, CI). Rule details: [`docs/RULES.md`](RULES.md).
**Current system map (generated, never stale): [`docs/architecture/overview.md`](architecture/overview.md).**

| Growing-game problem | Mechanism | §  |
|---|---|---|
| Hidden dependencies between features | asmdef graph + **internal Logic** + public Installer/Contracts | 1, 2, 6 |
| Prefab vs code, Inspector vs runtime data | **Prefab recipe + Variant**, single source of truth, required refs | 3, 4 |
| Replacing / deleting features | **Feature folder = code + assets**, Public/Private assets, `refs` | 5 |
| `public`/`partial`/string calls/reflection creeping in | static rules + reflection rules + API baseline | 6, 7 |
| AI forgetting rules | **hooks, pre-commit, Play Mode/build gates, protected CI** | 9 |
| Replay & async bugs | Lifetime tree, no mutable statics, lifecycle tests | 8 |
| Expensive test gate | pure tests always, Unity tests by impact | 10 |
| Following the whole system | **generated** system map + compact graph queries | 11 |
| Third-party SDKs/plugins | tiers + adapters + pinned, hash-locked vendors + time-boxed exceptions | 12 |

## 1. Dependency graph

```
            Game.Bootstrap ──► Game.MathQuiz.Unity ──► Game.MathQuiz (internal) ──► Game.Progress.Contracts
              │   (root)   └─► Game.Progress.Unity  ──► Game.Progress (internal) ──┘        │
              │                                                                            v
              └──────────────────────────────────────────────────────────────────────► Game.Core ──► UniTask
Editor-only:  Game.Bootstrap.Editor (gates, validators) · Game.MathQuiz.Editor (prefab recipe) · Game.Architecture.Rules
```
- Arrows point inward. Bootstrap sees only each feature's **Unity layer** (Installer, Module, asset entry) and Contracts.
- Logic is `internal` with `InternalsVisibleTo` only for its own Unity layer and tests. **Other assemblies cannot compile
  against it, even if someone adds an asmdef reference** (CS0122). ARCH012 reports such a reference before you even build.

## 2. Inside a feature

| Folder | Assembly | Visibility | Contains |
|---|---|---|---|
| `Contracts/` | `Game.F.Contracts` | **public**, locked by `PublicApi.txt` | interfaces/DTOs other features use |
| `Logic/Domain` | `Game.F` | internal | rules & entities (synchronous) |
| `Logic/UseCases` | `Game.F` | internal | sessions, presenters, view interfaces (async via `IDelay`/`Lifetime`) |
| `Unity/` | `Game.F.Unity` | internal + a small public face | views, ScriptableObjects, **Installer → Module**, **asset entry** |
| `Editor/` | `Game.F.Editor` | internal | prefab recipes, editor tools |
| `Tests/` | `Game.F.Tests` | n/a | pure NUnit (also run by `dotnet test`) |
| `Assets/Private/` | n/a | feature-only | prefabs, sprites, animations, config assets |
| `Assets/Public/` | n/a | referencable | the asset entry (`MathQuizAssets.asset`) and anything shared on purpose |

Public face of MathQuiz (everything else is internal):
`MathQuizInstaller.Install(assets, uiParent, random, delay, bestScore, lifetime, logger) → MathQuizModule { View, Start() }`,
`MathQuizAssets`, `QuizConfigAsset`, `QuizView`.
A public MonoBehaviour implementing an internal interface uses **explicit interface implementation** (`void IQuizView.ShowQuestion(...)`).

## 3. Prefab or code? Prefab recipe + Variant

Agents cannot drag & drop, and hand-edited YAML breaks silently. Artists need prefabs. So:

```
QuizViewLayout.cs (code, owned by devs/agents)
   │  Tools > Game > Prefabs > MathQuiz: Create Base Prefab   (or -executeMethod ...CreateBasePrefab in batch mode)
   v
Assets/Private/QuizView.generated.prefab     ← never hand-edited; regenerated from the recipe
   └── Prefab Variant (e.g. QuizView_SkinA)  ← artists: sprites, colors, fonts, layout tweaks
Assets/Public/MathQuizAssets.asset ─────────► points to the generated prefab or a Variant
GameRoot (scene) ───────────────────────────► MathQuizAssets
```
- **Prefab owns visuals, code owns logic & runtime state.** Views are passive (render view models, raise events).
- A component's serialized references point **inside its own prefab** (or to its feature's assets). Wiring between objects
  and features is done by Bootstrap/Installers in code: explicit order, no `FindObjectOfType`, no `Awake` races.
- **Reskin = new Variant + point the asset entry at it.** No code change.
- No prefab yet (fresh clone, tests)? The installer falls back to `QuizViewLayout.Create()`, so the game always runs.
- Caveat: *Force Rebuild* recreates objects (new file IDs), so Variant overrides on recreated objects can be lost.
  Prefer additive recipe changes once Variants exist; the ARCH072 validator reports broken references afterwards.

## 4. One source of truth for data

| Data | Lives in | Never in |
|---|---|---|
| Visuals (sprites, colors, layout) | prefab / Variant | code, scene |
| Gameplay config (ranges, timer, counts) | feature ScriptableObject (`QuizConfigAsset`) → `ToConfig()` → immutable `QuizConfig` | MonoBehaviour fields (ARCH070), scene overrides (ARCH081) |
| Runtime state (score, current question) | pure C# session (`QuizSession`) | MonoBehaviours, ScriptableObjects (ARCH071), statics (ARCH060) |
| Persistent data | repository (`PlayerPrefsProgressStore`), versioned + migrated | anywhere else |

Why ARCH070 matters: once a prefab or scene is saved, **the serialized value wins**. Changing `= 10` to `= 20` in C# does nothing
for existing instances, and every prefab, Variant and scene can hold a different value. Config therefore never sits on a MonoBehaviour.
Purely visual numbers may use `[VisualTuning]`.

Initialisation is explicit: `GameCompositionRoot.Awake` (execution order −1000) → infrastructure → `ProgressInstaller` →
`MathQuizInstaller` → `Start` → `module.Start()`. Views do nothing on their own in `Awake`/`Start` except hooking their own buttons.

## 5. Asset packaging per feature

- **A feature is one folder**: code (`Logic/ Unity/ Editor/ Contracts/ Tests/`) + assets (`Assets/Private`, `Assets/Public`) + README.
- **Isolation check without Unity (ARCH080):** every YAML reference is `guid: …`; `.meta` files map GUIDs to paths. The scanner
  flags any reference from outside feature X into X's non-Public assets in milliseconds (scenes/Bootstrap count as outside).
  This requires **Force Text** serialization (ARCH084, `ProjectSettings/EditorSettings.asset`).
- **Delete/replace a feature:** `./tools/test-gate.sh refs Assets/_Game/Features/X` lists incoming asset *and* assembly references;
  delete only when it prints nothing, then run the gate.
- No `Resources/` (string loads, ships everything; ARCH083); no string lookups (`Find`, `Shader.Find`; ARCH042).
  If streaming is needed later: Addressables with typed `AssetReferenceT<T>` inside the asset entry.
- UPM packages: use them for code shared across games/reskins (e.g. Core as a git package). Gameplay features stay in `Assets/`.

## 6. Access control (`private` / `internal` / `public`)

Default: `private` members, `internal` types, `sealed` classes. `public` only for:
Core (locked by `Core/Runtime/PublicApi.txt`), Contracts (locked by `Contracts/PublicApi.txt`), and the Unity layer's
Installer/Module/asset entry/views that Bootstrap or the Editor needs.

| Mechanism | Strength | Cost |
|---|---|---|
| `internal` + same-feature `InternalsVisibleTo` (ARCH020/021) | **compiler** refuses cross-feature use | free |
| Widening one type cascades into CS0050/CS0051/CS0053 errors | accidental widening rarely compiles | free |
| API baseline (ARCH023): reflection dump vs `PublicApi.txt`, diff printed | breaking a contract needs a code owner | seconds |
| No public mutable fields (ARCH022) | invariants stay inside the type | seconds |

## 7. Partial, string calls, reflection

- `partial` is banned in hand-written code (ARCH030): it splits files, not responsibilities. The real smell is size, so
  runtime files are capped at 300 lines (ARCH031). Generated code: `Generated/` folder **and** `// <auto-generated>` header.
- Calls the compiler cannot see are banned (ARCH040–044): `SendMessage/BroadcastMessage`, `Invoke("..")`, `StartCoroutine("..")`,
  reflection (`Type.GetType`, `GetMethod`, `Activator`, `dynamic`), `GameObject.Find/transform.Find`, `Resources.Load`, scene searches
  and singletons outside Bootstrap, Animator by string. Use interfaces, typed events and serialized references.
- Editor-only folders may use reflection (validators, tools); they never ship.
- Detection is regex on comment-stripped code: cheap and good enough for honest mistakes. Deliberate evasion (aliases) is
  the job of CI + code owners. A Roslyn analyzer DLL (`RoslynAnalyzer` label) is the next step if needed.

## 8. Lifetimes, replay, async (UniTask)

```
App ── quiz-loop (Lifetime.Run): NextFrame → TickScheduler → N × QuizBattle.Step() → render
```
- Gameplay timing lives in the **simulation** (§13), so restart is a `Start` command and there is no per-round async work to race.
  The general rules still apply to every other async flow (SDK calls, loading, network):
- `Lifetime.Run(token => ...)` is the only fire-and-forget; it tracks, cancels and logs. Guard after each await:
  `token.IsCancellationRequested || !IsStillCurrent(...)`; a state machine is the last guard.
- No mutable statics/static events (reflection rules), so **Enter Play Mode without Domain Reload** is enabled in
  `EditorSettings.asset` for fast iteration.
- Lifecycle tests: ×10 restarts keep one loop and 3 listeners, app dispose stops the loop, a frame source that ignores
  cancellation still stops it, `Rebind` swaps views mid-round, replaced views are collectable.

## 9. Enforcement layers ("the AI can't forget")

| Layer | When | What | Cost |
|---|---|---|---|
| **H0 Claude hook (PostToolUse)** | after every Edit/Write | static rules on that file; exit 2 feeds violations back | ~0.1 s, silent when clean |
| **H1 Compiler** | every build | `noEngineReferences`, internal Logic + IVT | free |
| **H2 dotnet gate** | `test-gate.sh run`, CI | all static rules, reflection rules, API baseline, all pure tests | seconds |
| **H3 Claude hook (Stop)** | agent tries to finish | runs the gate; exit 2 = keep working (max 3 blocks, then report); skipped in 0.1 s if nothing changed since green | seconds |
| **git pre-commit** | `git commit` (any tool/human) | static rules on staged files | ms |
| **H4 Unity Editor** | import / enter Play Mode | Console errors on import; **Play Mode is cancelled** while violations exist; required refs on prefabs/SOs/scenes | ms–s |
| **H5 Build gate** | player build | `BuildFailedException` on violations | s |
| **H6 CI + CODEOWNERS + branch protection** | PR | required checks; rule/gate/baseline files need an owner's review | CI |

One rule engine (`Assets/_Game/Architecture/Rules`) serves H0, H2, H3, pre-commit, H4 and H5, so each rule is written once.
Output is one line per violation plus links to the rule sections, which keeps it cheap for the agent to read.
Honest limit: an agent with shell access can still write files directly; that is why H6 (protected CI + owners) is the final authority.

## 10. Incremental test gate (cost)

| Gate | What | When |
|---|---|---|
| G0 | static rules on changed files (fail fast, ms) | always |
| G1 | `dotnet test LogicTests`: every rule + every pure test | pure code, pure tests, rules or gate changed |
| UI | layout gate of the **affected views only** (`-testCategory View:F.V`, §14) | a view's code/recipe/prefab/cases/baseline, its feature's view models/texts/fixtures |
| G2/G3 | Unity EditMode/PlayMode, **affected** assemblies only (`-assemblyNames`, from the asmdef graph) | integration files: installers, Bootstrap, scenes, settings, asmdefs |
| G4 | `--full` | pre-merge / nightly / release; any file with an unknown role (fail-closed) |

Each file is classified by its **role** (`ChangeImpact` in the rule engine, tested in `ChangeImpactTests`), not by guessing
from the diff: `tools/test-gate.sh plan` prints the role of every changed file and why.

Failures only, a few lines each; Unity retry budget (exit 4 after 2 consecutive failures).

## 11. System map: graph = data, diagram = view

One generator (`ArchitectureGraph` + `ArchitectureDocs` in `Assets/_Game/Architecture/Rules`) derives the graph from asmdefs
(incl. `precompiledReferences`), source, state tables and the policy file. Deterministic and fast, so it is **rebuilt every time**.

| View | Derived from | Where |
|---|---|---|
| L1 System (collapses to one node above 30 features) | folders + policy + asmdef refs | `docs/architecture/overview.md` |
| L2 Feature dependencies (only while ≤ 60 edges; else "most depended-on" table) | asmdefs + classes implementing other features' Contracts, also through base classes | overview |
| Compact feature table | all of the above | overview |
| Per feature: 1-hop diagram, public face, vendors, assets, tests, **state machines**, **DI (L3)** | same + `Transition<TState>` tables + constructors | `docs/architecture/features/<F>.md` |
| Impact / diff | reverse closure / committed docs vs now | `graph impact <F>`, `graph diff [base]` (on demand) |
| Hand-written docs | humans | `docs/`, READMEs, anchor-checked (ARCH091) |

**Who can change the generated files:** nobody by hand. Their content is only valid if it equals the generator output (ARCH090),
so a manual edit is caught by pre-commit/CI and overwritten by the next `graph --write`. Agents are also denied Edit/Write on
`docs/architecture/**`; CODEOWNERS covers the folder. Put human notes in hand-written docs.

**Can the map silently disagree with code?** Three guards: (1) stale → ARCH090 with the changed lines; (2) unreadable input →
ARCH090 parse error (fail-closed, e.g. a state-table row written as a helper call); (3) a reflection test compares every runtime
`Transition` table with the parsed diagram, so even a generator bug is caught. Known limits: plain type names in prose are
not anchor-checked; asset references are UNKNOWN until `.meta` files exist.

**Unity never blocks on documentation:** ARCH090/ARCH091 are excluded from the Play Mode, import and build gates; Unity logs a
warning and offers *Tools > Game > Regenerate System Map*. Hooks, pre-commit, the gate and CI still enforce them.

Measured (synthetic 150 features, 3,371 `.cs`, 3,000 prefabs, 300 MB SDK): after-edit hook 92 ms · `graph --write` ≈ 0.3 s ·
`check` of one file 0.56 s (vendor hash cached; 1.7 s on CI, which always re-hashes) · `check --all` 1.1 s ·
overview ≈ 4,700 tokens (was 16,000 before splitting), a feature page ≈ 300 tokens. This repo: overview ≈ 600 tokens.

## 12. Third-party plugins

Vendor code lives outside `Assets/_Game`, so the strict rules never scan it and it needs **no blanket exceptions**.
What is enforced is the contact surface, declared in `ArchitecturePolicy.txt` (protected; the only hand-written allowlist, because
none of this can be derived):

| Tier | Example | May be referenced by |
|---|---|---|
| `foundation` | UniTask | everyone, incl. pure code |
| `presentation` | DOTween, TextMeshPro | Unity layers only |
| `sdk` | ads, IAP, analytics, Firebase | **only its `adapter=` assembly**; others use the adapter feature's Contracts |

The sample's `Features/Ads` shows the pattern with a fake vendor (`Assets/ThirdParty/DemoAdsSdk`: singleton, static state,
background-thread callback): `IAdsService` (Contracts) ← `DemoAdsAdapter` (internal; callback → `UniTaskCompletionSource` →
cancellation → `SwitchToMainThread`) ← `AdsInstaller` (public face).

Unity traps the policy rules catch: DLLs left on *Auto Reference* (visible to every assembly, ARCH092); source plugins without an
asmdef (stuck in `Assembly-CSharp-firstpass`); edited vendor files or game code hidden in `Assets/Plugins` (hash + ownership, ARCH093);
SDK used outside its adapter or presentation libs in Logic (ARCH094). First-party debt can get a scoped, owned, expiring exception (ARCH095).

## 13. Deterministic simulation (Quiz Defense)

```
 view clicks ──► commands ──┐                 ┌──► state ──► presenter ──► view models ──► view (smooths, animates, VFX)
 frames + clock ─► TickScheduler ─► N × Step(tick) ─┤
 session seed ──► RandomStreams (keyed) ─────┘  └──► state hash + replay log (cmd/tick, checkpoints)
```

**Inputs that fully define a session:** seed · `SimVersion` · RNG version · configs (canonical text) · commands with their tick.
All of them are in the replay header/body, so a replay is self-contained (seed alone reproduces nothing once the player has acted).

| Concern | Decision | Why |
|---|---|---|
| RNG algorithm | own **xoshiro128\*\* + SplitMix64**, frozen by test vectors from an independent Python reference | `System.Random` is not guaranteed across .NET versions and Unity runtimes |
| RNG usage | **keyed streams** `For(purpose, round, id, tick)`: `quiz.operands/distractors/shuffle (round, n)`, `arena.spawn (round, i)`, `arena.penalty (round, i)`, `arena.hop (round, enemy, tick)` | no consumption drift: adding randomness somewhere never shifts another system; question N or enemy E can be reproduced alone |
| Seed derivation | FNV-1a 64 over bytes | `GetHashCode()` differs per process (measured), banned by ARCH055 |
| Time | **integer ticks** (20 Hz) inside the sim; `TickScheduler` (µs accumulator, max 8 ticks/frame, frames > 250 ms clamped = paused) outside | same result at 17/30/60/144 fps and with random frame times (tested); no drift of "10 s" timers |
| Countdown | deadline tick (`DeadlineTick`), time-out applied inside `Step` | exact: 200 ticks; never depends on frame timing or delay overshoot |
| Numbers | integers (lane = 1000 units, speeds per tick, percentages) | floats differ across CPUs/compilers; ARCH056 (incl. literals like `100f`) |
| Order | fixed per-tick order: commands → time-out → auto-next → arena (spawn, move by id, base hits) → defeat → checkpoint; enemies in id order | no dependence on `Update()` order or collection internals |
| Input | view events → commands queued → applied next tick, logged with that tick | live timing is non-deterministic by nature; the log makes it reproducible |
| External results | (pattern) adapters turn them into commands (e.g. a future `AdResult true`) | replays never call the network/SDK |
| State hash | `StateHashBuilder` (FNV-1a, fixed field order, enemies by id); checkpoint every 20 ticks + final | first divergent tick within 1 s; per-command hashing possible if needed |
| View | state → view models; events only for one-shot effects; `Rebind(view)` re-renders everything; view smooths positions in its own `Update` | views can be destroyed, replaced or run at any frame rate without touching gameplay (ARCH057) |

**Tests that prove it (all `dotnet test`, also Unity EditMode):** reference vectors · stream independence · keyed generator
(question N alone, distractor rules don't move operands) · scheduler at 17/30/60/144 fps + clamp · identical hash at tick 600 for
30/60/144 fps and random frame times · live play → replay bit-for-bit · tampered replay → first divergent tick · other `SimVersion`
refused · golden replays (`perfect` win, `afk` defeat, `chaotic` with restart) · zapping one slime doesn't change another's path.

**Mutation checks (deliberately broken, then reverted):** shared sequential hop RNG → 6 tests red, golden `afk` diverges at tick 40;
penalty 1 → 2 slimes → golden `chaotic` diverges at tick 20; `100f` float in the sim → ARCH056 (after closing a regex gap found this way).

**Cost:** 1 hour of game time (72,000 ticks, 12 slimes) simulates in ~0.27 s; verifying its 100 KB replay ~0.24 s.

**Bug workflow:** error → replay auto-saved (`persistentDataPath/replays/last-error.replay.txt`) or *Tools > Game > Debug > Save Replay*
→ drop into `Features/MathQuiz/Tests/Replays/` → `dotnet test --filter Category=Replay` reproduces it without Unity.
A golden replay failing = gameplay changed; intended changes are regenerated by an owner (`GOLDEN_WRITE=1`) and reviewed.

**Limits:** live input timing is not reproducible from the seed (only via the log); a ring-buffer log (`Capacity`) can't be replayed
from the start; IL2CPP equality is assumed from integer-only code + cross-runtime tests (Mono, CoreCLR); a device self-test replay is
the next step if a project needs proof on IL2CPP; physics must stay outside the deterministic core (record its results as commands).

## 14. UI: test a view, popup or component in isolation

**Goal:** sure, but not wasteful. Moving something 2px must run the layout checks of that one view, not the game.

**Why a diff classifier is not enough.** `400 → 402` and `score + 1 → score + 2` look the same in a diff. So the
architecture makes roles exact instead, and the gate only reads paths:
- views are passive (ARCH057, ARCH070): they render view models and raise events;
- static layout lives only in `Unity/Views/<V>/<V>Layout.cs` or the prefab (ARCH100); views may move things only from
  view-model data, on lines marked `// layout: dynamic`, without constants;
- every view has its folder, fixtures and cases (ARCH101), so a file or prefab (via its script GUID, Variants via
  `m_SourcePrefab`) maps to exactly one view.

**Layers** (each catches what the cheaper one can't):

| | Checks | Runs on | Blocking |
|---|---|---|---|
| L0 | presenter/flow/view models on a fake view; every scenario and fixture renders | dotnet | yes |
| L1 | prefab structure: required references, missing scripts (ARCH072/073) | import / Play / build gates | yes |
| L2 | **constraints**, not coordinates: safe area, overlap within a layer, clipped text (at best-fit min size), touch size; every fixture × 4 devices | Unity EditMode, no scene | yes |
| L3 | **text layout snapshot** of smoke + edge fixtures (one line per node: `Background/Question T 60,520 960x400`) vs a baseline owned by CODEOWNERS (`LAYOUT_WRITE=1`) | same run | yes; missing baseline = UNKNOWN = fail |
| L4 | boot, navigation, scene | PlayMode | only for wiring changes |

Screenshots are review artifacts (nightly / on prefab change), never a blocking gate: they depend on GPU and font rasterizer.

**Fixtures are typed C# (`ViewFixture<IView>`), not JSON**, and come in two kinds: hand-made view models (smoke, edge:
longest texts of the *shipped* config, crowded lists) and **scenario fixtures** (real simulation + real presenter). L3 uses only
the hand-made ones, so a gameplay change never forces a layout-baseline update; L2 checks all of them.

**Cheats = scenarios.** A scenario is seed + configs + commands through the real simulation (`QuizScenarios`): it is a cheat
("show me last-life"), a view fixture (`scn-last-life`) and a replay (`ToReplay()`; `FromReplay()` opens a bug report)
at once, and it can only produce states a player can reach. No field is ever set directly. Dev tools live in `DevTools/`
assemblies constrained to `UNITY_EDITOR || GAME_DEVTOOLS`; runtime code can't reference them and a release build that
defines `GAME_DEVTOOLS` fails (ARCH102). *Tools > Game > UI > View Preview* shows any view × fixture × device in the Scene
view with the same checks as the gate. New view: `test-gate.sh new-view <Feature> <View>`.

**Known limits:** the L2/L3 runner and the preview window are compile-checked here but not yet run in a real Unity Editor;
text measurement in `-batchmode -nographics` depends on Unity's font backend (if it misbehaves, drop `-nographics` for the
layout run). Width overflow of non-wrapping text and TextMeshPro are not measured yet (add to `LayoutProbe`). An in-game
dev menu (scenarios in a dev build) is the next step: it belongs in a `DevTools` assembly that the composition root does not
reference.

## 15. Agent control & the flight recorder (`tools/TestGate/Agent`)

**Principle:** the agent decides *what* to do; programs decide *whether it is allowed* and record *what really happened*.
No AI checks AI; no node of the graph comes from what an agent says.

| Concern | Mechanism | Where |
|---|---|---|
| Edits outside the task | **Task = permission unit**, scope derived from the architecture (feature folders), not a hand-written file list. One feature: the agent starts it itself. Wider (more features, Core, Bootstrap, paths, test expectations): `task request` → a human `task approve` (needs a terminal; refused when `CLAUDECODE=1` or stdin is piped; also denied in settings) | `AgentTask.cs`, PreToolUse |
| Edits via Bash | git snapshot before/after every Bash call → FILE_MODIFIED (source `git-observer`), same scope + per-file rules as Edit; reverts are always allowed | `WorkSnapshot.cs`, PostToolUse |
| Edits via MCP (Unity, Blender) | PreToolUse matches `mcp__.*`: the tool is classified (`McpPolicy`: name heuristic + owner lines in `tools/mcp-policy.txt`). Read: free. Write: needs a task, a named target path must be in scope, then a git snapshot before/after like Bash (FILE_MODIFIED via the tool). Exec (arbitrary code: it can write outside the repo): blocked unless the owner allows it. A Unity-serialized file that appears later in a session that used MCP (the editor saved it after the call) is judged as the agent's | `McpPolicy.cs`, `AgentHooks.PreMcp` |
| Changes no one saw | reconcile disk vs log at SessionStart/Stop → UNOBSERVED_CHANGE (shown, not hidden; often a human in the IDE) | `WorkSnapshot.Unexplained` |
| Fake fixes | diff guard on test files: fewer asserts/tests, new Ignore/Explicit/Assume/Inconclusive/Pass/`#if`, deleted tests → Stop blocked unless approved (`--tests`) | `TestGuard.cs`, Stop |
| Forgetting process | Stop hook runs scope + test guard + gate; SessionStart prints task, last gate, chain status (memory = project state) | hooks |
| "Tests pass" claims | TEST_RESULT only from the gate process, with the result file's hash; the agent's last message is a CLAIM judged against evidence | `Program.Run`, `Reducer` |
| Trustworthy graph | append-only JSONL, **hash chain** (edit/delete/reorder detected; `events verify`); graph = deterministic projection (`Reducer`), rebuildable at any time | `EventLog.cs`, `Reducer.cs` |

**Who caused the red (attribution).** A path's author is the last writer the log *proves*: FILE_MODIFIED from an agent session
= agent; UNOBSERVED / baseline / never seen = human. At Stop every violation and failing test is classified (`Attribution.cs`):
a violation on a changed file belongs to that file's author; an asset/plan rule (ARCH073, 080–084, 093, 110–111, 120, 130–132)
when the agent touched no YAML/plan/asset is the human's; failing tests are the agent's if it changed code *or nobody else did*
(an unexplained red is never pushed onto the human); a weakened test the human edited goes to the human. If only human fixes
remain, Stop returns 0, writes `.agent/human-fixes.json`, emits HUMAN_FIX_NEEDED; SessionStart and the dashboard ("Chờ NGƯỜI sửa")
show it with human commands (undo vs. intended: e.g. bump SimVersion + `GOLDEN_WRITE=1`). Pre-commit and CI still block the merge:
attribution decides *who must act*, never *whether it is green*.

**Edits from the Unity editor.** Design-lint tests read the shipped `QuizConfigAsset` YAML (`ShippedContent`), not only code
defaults, so an inspector value that breaks a `[story]` goal fails like code. Orphan files need proof a session ended (a later
SessionStart of another session); task locks whose worktree vanished or whose task closed are pruned on every registry write.

Trust levels on every node: planned → observed → verified (a passing gate after it) / pending (Unity not run) / failed;
plus denied (blocked before), violation, unobserved, claim.

**Dashboard = data + a static view.** `dash` writes only data: `TestResults/dashboard/data.json` (+ `data.js`, the same as
`window.MODEL = …`, because a page opened from disk may load a script but not fetch a JSON) and `sessions/s-<id>.js` (one
session's steps, loaded when clicked; only the listed sessions are kept). The view is `tools/dashboard/` (`index.html`,
`style.css`, `app.js`): hand-written, never generated. `serve` serves both and refreshes the data; `dash --bundle` inlines
everything into one HTML to send. The page: 1 what the human must do · 2 the plan as a **node graph** (Plan DAG: features,
hard/contract/soft edges; or a table) · 3 execution as a **node graph** (one column per agent, one node per event, coloured by
trust, click = the raw event and its hash; one session at a time) · 4 anomalies · details. `data.json` is a **snapshot** (`SnapshotBuilder`): what the human must do, the roadmap,
recent anomalies (≤30), sessions (≤30, one line each): its size follows the project, not the history (test: 5k vs 50k
events). Agents never read it: the CLI answers them. The view may only read fields the data has (test parses `app.js`).
Reading the log: one pass over the bytes (`ParseUtf8`), and the chain is verified incrementally from a checkpoint
(`events.jsonl.verified`: count, bytes, SHA-256 of the prefix, head). An old edit changes the prefix digest → full check; a
log shorter than the checkpoint is reported (tail truncation). Read/Grep/Glob are no longer logged.

**Honest limits.** The log is tamper-*evident*, not tamper-proof: an agent with a shell running as the same user could rewrite the whole
file or truncate its tail together with the checkpoint (anchor the head hash, e.g. in CI or a commit trailer; run the recorder as another user for stronger
guarantees). CI re-running the gate is the final authority. Hook input fields (agent ids, transcript format) are not a stable contract:
missing fields degrade to "main"/no claim, never to a crash. Commands run outside Claude Code's tools are only seen by the
reconcile step. Codex: `.codex/config.toml` marks its shells (`GAME_AGENT_SHELL`), so human-only commands refuse there, and
pre-commit/CI check its work; its hooks (PreToolUse/PostToolUse/Stop) are not wired to `claude-hook.sh` yet (field names to be
verified against a real Codex). Human-only commands refuse in any shell carrying an agent marker (`CLAUDECODE`, `CLAUDE_CODE_*`,
`CODEX_*`, `GAME_AGENT_SHELL`, ...) or without a terminal; an agent that scrubs its whole environment *and* fakes a terminal is
still stopped by the PreToolUse command check and the settings deny list, not by this test.

## 16. Roadmap: plan as data, status as evidence

Problems: the agent forgets the plan in long work; the human has no feature list to control; plan changes mid-work leave
agents building the old version; "rush" silently skips dependencies. Full guide: `docs/PLANNING.md`.

- **Plan = `plan/features.md`, owned by the human** (deny + CODEOWNERS), strict Markdown parsed by the rule engine (ARCH110).
  Two levels the human manages: **Feature** (a capability a player can accept) → **Requirement** (one testable behaviour).
  No task list: tasks are derived from gaps when work starts (`roadmap next`), so nothing is forgotten and nothing goes stale.
- **Status is computed, never set:** tests carry `[Covers("R-ID")]` (ARCH111); a requirement is VERIFIED only if its tests
  passed after the last change of its modules; `[manual]` ones need a human `roadmap accept` for that exact wording.
  The gate records verified wording in a ledger, so a requirement the human rewords becomes **STALE** until a covering test
  changes (re-running old tests doesn't count).
- **Plan changes:** a task stores the hash of each requirement it started with; reworded / removed / deferred / dropped →
  the task is STALE and Stop is blocked with the new wording. The human's own edits are UNOBSERVED changes, not the agent's.
- **Parallel + rush:** one worktree per agent; a registry in the git common dir refuses overlapping modules. `hard`
  dependencies are never skipped; `contract` ones may be with `[rush]` if the interface is frozen (in a `PublicApi.txt`),
  which creates **debt**: the feature can't be DONE until the provider is DONE and its tests ran again ("làm bù").
- **Gaps** (agent vs human): FAILING, STALE, UNVERIFIED, NOT_RUN, NO_TEST, DEBT, NEEDS_REVIEW, NO_REQUIREMENTS,
  UNPLANNED_CODE, PROPOSAL. `roadmap next` turns agent gaps into parallel lanes with the exact `task start` command.
- **Memory:** SessionStart prints the roadmap summary, STALE warnings and the next 3 items (computed from files).
- **Dashboard:** tabs *Tính năng* (feature table + requirements + next + gaps), *Plan DAG* (dependency graph: hard / contract /
  soft, rush border, debt, status colours) and *Thực thi* (the execution graph of §15). All projections of files + events.

## 17. Story · Gameplay · Art (three branches, different speeds)

The failure mode: story fixes content ("wolf, 3 skills"), gameplay codes it, art can't deliver, everything is redone; or
gameplay waits for art. The fix keeps each branch's truth where it can be checked:

| Branch | Owns | Checked by |
|---|---|---|
| Story | experience goals (`[story]` requirements) | design-lint tests over content data (`ArenaDesign.Intents`, `[Covers]`) |
| Gameplay | rules, timing in ticks, footprint (simulation) | pure tests, golden replays |
| Art | visuals inside a slot contract (`ArtSlotContract`: envelope, pivot, action markers in tick windows) | art gate (Unity EditMode) + human approval by content hash |

- Content swaps (archetypes) are data; when a story goal breaks, the lint names it with numbers (e.g. the bat candidate:
  "breather: first contact after 83 ticks < 100") and the human decides. No change-request bureaucracy for swaps that keep the goals.
- Graybox = generated from the contract → always conforms → gameplay never waits for art.
- Art level (GRAYBOX/CANDIDATE/APPROVED/FINAL, STALE after edits) and ART_DEBT are computed; `[art-risk]` + `[feasibility]` puts the
  art question *before* deep gameplay work. Dashboard: Story/Gameplay/Art columns, an *Art* tab with slots and the art lane.
- Limits: the art gate's measuring runs only in the Unity Editor (here: compiled against stubs; pure checks tested). 3D rig
  compatibility is out of scope (contracts cover size, pivot and timing). "Looks good" stays a human `art accept`.

## 18. Files: no registry, nothing to reconcile

An asset registry (a copy of the filesystem) drifts the moment someone drags a file in Finder, and then needs a reconciliation
engine. Here every question is answered from sources that are always right:

| Question | Answered by |
|---|---|
| Where may it live / which feature owns it? | its path (ARCH130); moving a file needs no bookkeeping |
| Is it junk? | its name and place (ARCH131) |
| Is it official? | git: tracked vs untracked |
| Who made it, did that session finish, is it unchanged? | the hash-chained event log (FILE_MODIFIED, STOP_ALLOWED, content hash) |
| What is its identity, who uses it? | `.meta` GUID + YAML references (ARCH132, `files unused`) |

- Prevention: hooks flag misplaced/junk files on the edit; Stop names the leftovers of the session; `.scratch/` for temp work;
  agents may not write `.unity` files.
- Cleanup (`files clean`) deletes only files that are untracked AND written by an observed agent AND unchanged since (and not a
  proposal for the human); everything else is reported for a human. Dry run unless `--yes`. Crashed sessions are found from the log
  (writes without a later clean Stop); no leases, heartbeats or time thresholds.
- `files unused` walks the GUID graph from scenes, `Assets/Public`, ProjectSettings and asset paths written in code; report only
  (Addressables-style string loading would look unused; git keeps deleted files anyway). `files du` shows what takes space and
  `files clean --regenerable` removes only what tools recreate (Library only with `--library`, never by agents).
- Manual moves: a move without `.meta` makes Unity mint a new GUID and silently breaks references. ARCH132 judges only GUIDs that
  existed in the project (deleted `.meta` since the branch base), points at the referencing line and prints the fix (restore the
  old `.meta` at the new path, found by git blob equality), or the restore command if the asset was deleted.
- Other AIs and Claude cloud sessions: no separate intake. Everything arrives through git, so the same pre-commit/CI rules apply;
  only Claude sessions (hooks) get per-edit feedback and authorship in the log; other tools' files show as "unknown origin" and are
  never auto-deleted.

## 19. Next tasks for an agent
- Pause button (`Time.timeScale = 0` stops `UnityGameClock`, so no ticks run) + a test with `FakeClock`.
- Rewarded ad "+5 s": MathQuiz depends on `Game.Ads.Contracts`, awaits `IAdsService.ShowRewardedAsync(token)`, re-checks the question.
- Currency feature (`./tools/test-gate.sh new-feature Currency --contracts`) with its own asset entry in `Assets/Public`.
