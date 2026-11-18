# Feature: MathQuiz ("Quiz Defense")

An addition quiz (pick 1 of 3 within 10 s) inside a small real-time arena: slimes spawn on the right with random speeds,
sometimes hop in place, and walk to the base on the left. **Correct answer → the slime closest to the base is zapped.
Wrong answer or time-out → slimes rage (faster) for 2 s and an extra slime spawns.** A slime reaching the base costs a life;
0 lives ends the round (defeat). After N questions the round ends normally. The whole game is a deterministic simulation.

## Responsibility
- Generate questions with 3 distinct, non-negative, nearby options (exactly one correct).
- Run a round: answer or time-out → feedback → next → ... → summary → restart.
- Run the per-question countdown (async, UniTask) and cancel it correctly.
- Report the final score to Progress (via `IBestScoreService`).

## NOT responsible for
- Saving anything (Progress does it). Sound, ads, currency.

## Flow (state machine in `QuizSession`)
```
NotStarted --Start--> AwaitingAnswer --SubmitAnswer | TimeOut--> Answered --Advance--> AwaitingAnswer
                                                                          \--(last)--> Finished --Start--> AwaitingAnswer
```
Invalid calls (double tap, tap after time-out, Next before answering) return a failed `Result`; they never throw.
The real table is `QuizSession.Transitions` (`Transition<QuizState>` rows): every move is checked against it
(`TransitionTable.Ensure`) and the state diagram in `docs/architecture/overview.md` is generated from it.

## Simulation (`Logic/Simulation`)
- `QuizBattle`: fixed tick (20 Hz) simulation: commands → time-out → auto-next → `Arena` → defeat → hash checkpoint.
- `QuizCommand`: `Start`, `Answer <i>`, `Next` (text form is the replay format).
- Random streams: `quiz.operands`, `quiz.distractors`, `quiz.shuffle` (round, n) · `arena.spawn` (round, i) ·
  `arena.penalty` (round, i) · `arena.hop` (round, enemy, tick). Renaming one changes every replay.
- `QuizBattleReplay.Verify(text)`: re-runs a replay, refusing other `SimVersion`/RNG/tick rates.
- Golden replays: `Tests/Replays/*.replay.txt` (`perfect`, `afk`, `chaotic`) + any bug report you drop there.

## Lifetimes, time and async
```
parent (App, from Bootstrap)
 └─ quiz-loop  ONE loop for the presenter's life: NextFrame → clock → TickScheduler → N × battle.Step() → render
```
- Restart is a `Start` command, not a new loop: there is no per-round async work left to race.
- Frames/clock: `UnityFrameSource` + `UnityGameClock` in the game, `ManualFrames` + `FakeClock` in tests.
- After the await: `token.IsCancellationRequested || disposed` → return (tested with a frame source that ignores cancellation).
- `QuizPresenter.Rebind(view)` swaps the view mid-round and re-renders from state.

## Public face (everything else is `internal`)
| Type | Assembly | Use |
|---|---|---|
| `MathQuizInstaller.Install(assets, uiParent, streams, clock, frames, bestScore, lifetime, logger)` | Game.MathQuiz.Unity | the only way Bootstrap builds the feature |
| `MathQuizModule` (`View`, `Start()`, `Seed`, `ExportReplay()`) | Game.MathQuiz.Unity | returned by the installer |
| `MathQuizAssets` (ScriptableObject) | Game.MathQuiz.Unity | **asset entry**: config + view prefab, lives in `Assets/Public/` |
| `QuizConfigAsset` (ScriptableObject) | Game.MathQuiz.Unity | designer config (range, questions, seconds, arena in seconds → converted to ticks once) |
| `QuizView` (MonoBehaviour) | Game.MathQuiz.Unity | view; `IQuizView` is implemented explicitly |

Internal (visible to `Game.MathQuiz.Unity` + tests via `Logic/AssemblyInfo.cs`): `QuizConfig`, `ArenaConfig`, `Question`,
`QuestionGenerator`, `QuizSession`, `QuizBattle`, `Arena`, `QuizCommand`, `QuizPresenter`, `IQuizView` + view models, `QuizTexts`. `QuizViewLayout` is
internal to the Unity layer (visible to `Game.MathQuiz.Editor`).

## Dependencies
- `Game.Core`: `IRandomStreams`, `IGameClock`, `IFrameSource`, `TickScheduler`, `StateHashBuilder`, `ReplayLog`, `Lifetime`, `IGameLogger`, `Result`, `[OptionalReference]`, `[VisualTuning]`
- `Game.Progress.Contracts`: `IBestScoreService`
- `UniTask`

## Assemblies & folders
| Folder | Assembly | Notes |
|---|---|---|
| `Logic/` (`Domain/`, `UseCases/`) | Game.MathQuiz | pure, `noEngineReferences`, internal |
| `Unity/` | Game.MathQuiz.Unity | namespace `Game.MathQuiz.Presentation` |
| `Unity/Views/QuizView/` | (Game.MathQuiz.Unity) | `QuizView` (passive view) + `QuizViewLayout` (layout recipe: the only C# with positions/sizes, ARCH100) |
| `Editor/` | Game.MathQuiz.Editor | `QuizViewPrefabRecipe`, `Views/QuizViewCases` (how the layout gate/preview create QuizView) |
| `DevTools/` | Game.MathQuiz.DevTools | `Fixtures/QuizViewFixtures`, `Scenarios/QuizScenarios` (pure; Editor + `GAME_DEVTOOLS` builds only) |
| `Tests/` | Game.MathQuiz.Tests | pure NUnit |
| `Assets/Public/` | n/a | `MathQuizAssets.asset` (created by the recipe) |
| `Assets/Private/` | n/a | `QuizView.generated.prefab` (recipe output) + skin Variants |

## Content, story goals and art
- `Logic/Domain/EnemyArchetype.cs`: enemy kinds as data (slime ships; bat is a candidate). `ArenaConfig.WithArchetype` swaps content
  without changing the replay format.
- `Logic/Domain/ArenaDesign.cs`: the story's experience goals (`breather`, `readable`) as integer checks; `Tests/DesignLintTests`
  covers `R-ARENA-BREATHER` / `R-ARENA-READABLE`.
- `Logic/Domain/MathQuizArtSlots.cs`: art slot contracts (`MathQuiz.Slime`, `MathQuiz.Bat`). The slime marker of `QuizView` is the
  slot's generated graybox; bind real art with an `ArtSlotAsset` (slotId `MathQuiz.Slime`) referenced from `QuizView.slimeArt`.

## Testing the view in isolation
| Layer | What | Where | Cost |
|---|---|---|---|
| L0 | presenter, flow, view models (FakeQuizView), every scenario + fixture | `Tests/` (dotnet) | ms |
| L1 | required references of the prefab | ARCH072 import/Play/build gates | ms |
| L2 | safe area, overlap, clipped text, touch size: every fixture × 4 devices | `Game.Core.Editor.Tests`, category `View:MathQuiz.QuizView` | EditMode, one view |
| L3 | text layout snapshot (smoke + edge fixtures) vs `Tests/Layout/QuizView.layout.txt` | same | same |
| L4 | boot the game | `Game.Bootstrap.Tests` (PlayMode) | only for wiring changes |

Look at a state by hand: *Tools > Game > UI > View Preview* (same fixtures, same checks as the gate).
A bug report replay opens as a scenario: `QuizScenario.FromReplay("bug-123", text)`.

## Prefab workflow
1. Change UI structure in `Unity/Views/QuizView/QuizViewLayout.cs` (the recipe), never in prefab YAML.
2. `Tools > Game > Prefabs > MathQuiz: Create Base Prefab` (batch: `-executeMethod Game.MathQuiz.EditorTools.QuizViewPrefabRecipe.CreateBasePrefab`).
3. Artists create a Prefab Variant of `QuizView.generated.prefab` for skins and point `MathQuizAssets.viewPrefab` at it.
4. Without any prefab the installer builds the UI from the recipe at runtime.

## Invariants (each has a test)
- Exactly 3 distinct options; `Options[CorrectIndex] == A + B`; all `>= 0`, within `±MaxDistractorOffset`.
- Correct position is shuffled; same seed → same questions.
- A question scores at most once; a time-out scores 0 and reveals the answer.
- Answering stops the countdown (no pending delays).
- Restart mid-countdown: old countdown is cancelled; it never fires into the new round,
  **even if the delay implementation ignores cancellation**.
- Replay ×10: 1 handler per view event, 0 active tasks in old rounds, old rounds garbage-collectable.
- Best score is submitted exactly once per round.

## Where to change what
| Task | File |
|---|---|
| Difficulty / ranges / seconds | `Unity/QuizConfigAsset.cs` (data) or `Domain/QuizConfig.cs` (rules) |
| How wrong options are picked | `Domain/QuestionGenerator.cs` + `Tests/QuestionGeneratorTests.cs` |
| Scoring / flow | `UseCases/QuizSession.cs` + `Tests/QuizSessionTests.cs` |
| Countdown behaviour | `UseCases/QuizPresenter.cs` + `Tests/QuizTimerTests.cs` |
| Replay / cancellation | `UseCases/QuizPresenter.cs` + `Tests/QuizLifecycleTests.cs` |
| Texts | `UseCases/QuizTexts.cs` |
| Layout / colors / timer bar | `Unity/Views/QuizView/QuizViewLayout.cs` (recipe) then rebuild the prefab; skins in a Variant |
| What Bootstrap passes in | `Unity/MathQuizInstaller.cs` |
| A state to look at / a cheat | `DevTools/Scenarios/QuizScenarios.cs` (commands only) → appears as fixture `scn-<name>` |
| An edge case of the screen | `DevTools/Fixtures/QuizViewFixtures.cs` (hand-made view models) |

## Tests
```bash
dotnet test LogicTests --filter FullyQualifiedName~Game.MathQuiz
dotnet test LogicTests --filter TestCategory=Lifecycle
```
