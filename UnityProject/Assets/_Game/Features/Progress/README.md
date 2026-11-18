# Feature: Progress

## Responsibility
- Keep the player's best score across sessions.
- Load, migrate and repair saved data (corrupted saves never crash the game).

## Public face (everything else is `internal`)
| Type | Assembly | Use |
|---|---|---|
| `IBestScoreService` (`BestScore`, `SubmitScore`) | Game.Progress.Contracts | what other features use. **Locked by `Contracts/PublicApi.txt`** |
| `ProgressInstaller.Install()` → `ProgressModule` (`BestScore`, `Flush()`) | Game.Progress.Unity | Bootstrap only |
| `ProgressInstaller.PlayerPrefsKey` | Game.Progress.Unity | Editor "Reset Saved Progress", tests |

Internal: `BestScoreService`, `IProgressStore`, `ProgressData`, `ProgressMigrator` (Logic, visible to `Game.Progress.Unity` and
tests), `PlayerPrefsProgressStore` (Unity layer).

## Assemblies & folders
| Folder | Assembly | Unity? | References |
|---|---|---|---|
| `Contracts/` | Game.Progress.Contracts | no | (none) |
| `Logic/` | Game.Progress | no | Game.Core, Game.Progress.Contracts |
| `Unity/` | Game.Progress.Unity (namespace `Game.Progress.Infrastructure`) | yes | Game.Core, Game.Progress, Game.Progress.Contracts |
| `Tests/` | Game.Progress.Tests | no | Game.Progress, Game.Progress.Contracts |
| `Assets/Public`, `Assets/Private` | n/a | n/a | (no assets yet) |

## Rules / invariants
- `BestScore >= 0`.
- The constructor has no side effects; the installer calls `Load()`.
- `SubmitScore` stages data (`Save`) and never writes to disk (`Flush`). Bootstrap flushes on pause/quit.
- Changing `ProgressData`: bump `CurrentVersion` and add a migration step + test.
- Changing `IBestScoreService` is a contract change: ARCH023 fails until a code owner updates `PublicApi.txt`.

## Tests
`Tests/BestScoreServiceTests.cs`: load, repair, version fallback, submit rules, no flush.
