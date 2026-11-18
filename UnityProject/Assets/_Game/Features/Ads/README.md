# Feature: Ads

Example of a **third-party SDK boundary**. The vendor (`Assets/ThirdParty/DemoAdsSdk`, a fake SDK written
like real ones: singleton, static state, background-thread callbacks) is registered in `ArchitecturePolicy.txt`
as tier `sdk` with `adapter=Game.Ads.Unity`.

## Responsibility
- Expose ads to gameplay as `IAdsService` (Contracts) with no SDK types.
- Adapt the SDK: callback → `UniTaskCompletionSource` → cancellation → main thread.

## Public face
| Type | Assembly | Use |
|---|---|---|
| `IAdsService.ShowRewardedAsync` | Game.Ads.Contracts | other features (locked by `Contracts/PublicApi.txt`) |
| `AdsInstaller.Install(appKey)` | Game.Ads.Unity | Bootstrap only |

`DemoAdsAdapter` is internal. **Only `Game.Ads.Unity` may reference `DemoAdsSdk`** (ARCH094). The vendor folder is
hash-pinned (ARCH093): never edit it; upgrades are re-approved by a code owner (`./tools/test-gate.sh vendor-hash <folder>`).

## Not wired yet
No feature consumes ads yet. Next task for an agent: "rewarded ad +5 seconds" in MathQuiz. Add `Game.Ads.Contracts`
to MathQuiz; the adapter result enters the simulation as a COMMAND (e.g. `AdResult true`) so replays never call the SDK.
Bootstrap passes `AdsInstaller.Install(...)` into `MathQuizInstaller`.
