# Product plan: features, requirements, gaps, rush

The plan is **data the human owns** (`Docs/Plan/features.md`), not a prompt. Agents propose changes in `Docs/Plan/Inbox/`, or,
when the owner asks, write it after the owner's yes/no for that exact change (see "Changing the plan with an AI"). Nothing in it says "done": every status is **computed** from tests and evidence.

```
Docs/Plan/features.md   the plan (human; CODEOWNERS; denied to agents)          ARCH110 checks it on every gate/commit
Docs/Plan/accepted.txt  human acceptances of [manual] requirements (`roadmap accept`)
Docs/Plan/Inbox/*.md    proposals written by agents (`roadmap propose`), decided by the human
[Covers("R-ID")]   on tests: the link requirement → evidence (ARCH111: the id must exist)
.agent/plan-ledger.json   requirement wording at its last verification (STALE detection; written by the gate)
```

## Granularity

| Level | What it is | Who | Size |
|---|---|---|---|
| **Feature** `F-…` | a capability a player can see and accept on its own ("Đếm giờ mỗi câu") | human | 2–8 requirements |
| **Requirement** `R-…` | one testable behaviour ("Hết giờ đúng tick hạn chót") | human (agents propose) | 1–few tests |
| Task | one piece of work: one code scope (feature folder), ≤ 1 session, ≥ 1 requirement | **derived from gaps** (`roadmap next`) | |
| Evidence | tests with `[Covers]`, human acceptances | code / human | |

There is **no task list in the plan.** Tasks are started from gaps when work begins, so nothing goes stale when the plan
changes and nothing is forgotten when a session ends. A feature is not a folder, a class or a screen: `[modules: X]` says
which code folders it lives in (task scope and change impact use it).

## Syntax (parsed strictly; unknown lines/tags are ARCH110 errors with a line number)

```markdown
# Plan: Quiz Defense

## F-TIMER  Đếm giờ mỗi câu  [P0] [modules: MathQuiz]
Any free text line is the description.
- R-TIMER-DEADLINE  Hết giờ đúng tick hạn chót và tính là trả lời sai
- R-TIMER-FEEL  Đồng hồ dễ đọc  [manual]
deps: hard F-QUIZ-CORE · contract F-ADS (IAdsService) · soft F-ARENA
```

- Feature tags: `[P0]`…`[P3]` (default P2), `[modules: A, B]`, `[rush]`, `[deferred]`, `[dropped]`.
- Requirement tags: `[manual]` (verified by a human with `roadmap accept`, e.g. look and feel), `[story]` (an experience goal
  of the story, checked by design-lint tests over content), `[art]`, `[feasibility]` (manual: "can art deliver this?").
- Art (see "Story · Gameplay · Art" below): `[slots: F.Name, ...]` the art slots the feature shows, `[art: graybox|candidate|approved|final]`
  the art level it needs to be DONE, `[art-risk]` gameplay requirements wait until the `[feasibility]` ones are accepted.
- Dependencies:
  - `hard F-X`: needs F-X **DONE**. Never skipped, not even by rush.
  - `contract F-X (IName)`: needs only the interface. Normally F-X DONE; with `[rush]` it is enough that `IName` is
    **frozen** (listed in a `PublicApi.txt`), work proceeds against a fake, and an **integration debt** is tracked.
  - `soft F-X`: preferred order only.
- Ids are stable: rename the title freely, never the id (tests point to it).

## Computed statuses

Requirement:

| Status | Meaning | Gap for |
|---|---|---|
| VERIFIED | every covering test passed after the last change of its modules (and of hard/contract dependencies' modules) | |
| ACCEPTED | `[manual]`, accepted by a human for this exact wording | |
| NO_TEST | no `[Covers]` | agent |
| NOT_RUN | covering tests have no result yet | agent |
| FAILING | a covering test failed | agent |
| UNVERIFIED | code changed after the last test run | agent |
| STALE | the human reworded it after it was verified, and no covering test changed since (re-running old tests doesn't count) | agent / human |
| NEEDS_REVIEW | `[manual]`, waiting for a human | human |
| UNSEALED | tests exist but no independent session sealed them (or the wording changed since, or red first is pending on Unity) | agent (role tests) |
| TAMPERED | sealed tests changed after sealing | agent (role tests/arbiter) |
| DISPUTED | the impl session says a sealed test is wrong: an arbiter rules; escalated (`ask`, or the 3rd dispute) it is the owner's | agent (arbiter) / human |

Feature: **DONE** = every requirement VERIFIED/ACCEPTED, every hard and contract dependency DONE, no debt.
Otherwise IN_PROGRESS / PLANNED / BLOCKED (a hard dependency not done and nothing started) / DEFERRED / DROPPED.
Progress is shown as "5/7 requirement đã kiểm chứng": never a guessed percentage.

Other gaps: `DEBT` (rush integration still owed), `NO_REQUIREMENTS`, `UNPLANNED_CODE` (a feature folder changed that no
active feature lists in `[modules]`), `PROPOSAL` (inbox waiting for the human).

## Separation of duties: who writes the tests

Code: `Tools/TestGate/Agent/TestRoles.cs`, `TestSeal.cs`, `SealStore.cs`, `TestLint.cs`. Three roles, each a separate session
(`task start --role …`; PreToolUse refuses a session that sealed the tests to write their code, or one that wrote or disputed them to rule):

| Role | Writes | Ends with |
|---|---|---|
| `tests` | `Features/<F>/Tests/**` + a skeleton in `Logic/`/`Contracts/` (declarations, `=> default`, `return default;`, `{ }`) | commit, `tests seal R-…` |
| `impl` | the feature's code; never `Tests/**` | sealed tests green; or `tests dispute R-… "<why>"` |
| `arbiter` | like `tests` | `tests rule R-… keep \| fix \| ask "<why>"` (fix → seal again) |

- **Seal** (`Docs/Plan/Seals/<F-ID>.txt`, one append-only log per feature, written only by the gate): the covering tests were written in
  this task by this session and committed; **lint** (an assertion, none of `Assert.Pass`, literal/self comparisons, swallowing catch);
  **red first** (`dotnet test --filter` on just those tests, twice: they build, both runs agree, at least one fails on an assertion,
  not only by an exception). A re-seal is red-checked against the old seal's commit in a temporary worktree. Unity-only tests seal as
  `red=pending` and count once CI/Unity confirms.
- **Gate:** sealed tests of a requirement that never passed are red by design (reported, not a failure). Once they pass the gate
  appends `verified R-…` to the seal log (tracked, so CI knows): red after that is a regression.
- **Disputes never wait for the owner:** the impl session moves on; an arbiter session rules. The 3rd dispute of one requirement goes
  to the owner automatically, as do the rewording proposals (one yes/no in chat). An author with 2 "fix" rulings among their last 5 seals
  gets their other seals re-checked (DISPUTED) before impl runs into them.
- **Parallel:** tasks conflict only if they share modules AND write the same folders: a `tests` and an `impl` task of one feature run
  side by side, two `impl` tasks of one module don't. `roadmap next` gives one item per (feature, role).
- **Owner's last resorts** (terminal, denied to agents): `tests unseal R-… "<why>"`, and once when adopting this: `tests grandfather`
  (today's covering tests count as sealed, `author=legacy`).

## Change requests between modules (CR)

Code: `Tools/TestGate/Agent/CrCli.cs`, `CrClassifier.cs`, `CrStore.cs`, `Planning/CrPlanning.cs`; log parsing `CrLog` in
`Architecture/Rules/PlanRules.cs`. Problem it closes: session A asks session B (by message) to "just change X"; B's task is valid
for B's folder, so the gate saw nothing wrong. Now every change traces to an approved R- or CR, whoever asked.

- `task need <F-ID|Module|Core> "<what, why>" [--api "<PublicApi line>"]… [--patch .scratch/x.diff]` appends to
  `Docs/Plan/CRs/<target>.txt` (append-only, written only by the gate: `CR_WRITTEN` events; protected). Numbers are unique across
  worktrees (counter in the git common dir).
- **Across worktrees:** every write also goes to a mirror in the git common dir (`agent-control/crs/`), so B's worktree sees A's CR
  before A's branch is merged. The same line in both logs is one event. `task start --from CR-n` copies the CR's lines into this
  branch's tracked log (a gate write: commit it), so the branch carrying `[Covers("CR-n")]` also carries the CR (ARCH111 on CI).
- Who decides is computed from the API diff, not from what the asker says:

| Kind | What | Decides |
|---|---|---|
| `additive` | only new types in B's `Contracts/` | the gate at once; the asking task may write B's Contracts: exactly those PublicApi lines + a skeleton |
| `breaking` | removes/changes a line, or adds a member to an existing type (breaks implementers and fakes) | an arbiter: another session, `cr rule CR-n keep \| reject \| ask` |
| `meaning` | no `--api`: B's behaviour/requirement | the owner, batched: `cr decide` (terminal only) |
| `core-additive` | new types in Core/Runtime | an arbiter |
| `core-breaking`, `core-meaning` | | the owner; done in a core task of its own, closed with `cr deliver` |

- **Approved = requirement of B:** CR-n joins B's feature (tag `[cr]`, features.md untouched): `[Covers("CR-n")]`, seals, roles,
  `roadmap next` work like any R- (`task start … --from CR-n` = `--req CR-n`; a task on an API CR gets the same Contracts grant).
- **Grants arrive late too:** when an arbiter or the owner approves an API CR, the asker's task (if still open, in any worktree)
  gets its Contracts grant at once; a later task of the asking feature gets it at start while the CR's lines are not in place.
- **Frozen interface** (`contract F-X (IName)`): a type named exactly `IName` is in a PublicApi.txt baseline, and no open or approved
  CR is still changing its members (lines not in place yet). A word somewhere in the file is not enough.
- **Nobody waits idle:** the asker works on a fake; its feature carries debt `chờ … CR-n` and is never DONE until CR-n is VERIFIED.
  CR work runs at the priority of the feature waiting for it; approved and undone after 24 h = `CR_OVERDUE` for the owner.
- **Provenance:** a task without `--req`/`--from` is maintenance. `UNTRACED_CHANGE` at Stop when it changes `Contracts/` or
  `Logic/Domain/`, or when a `[Covers]` test of its features that was red at task start is green after the gate (behaviour delivered
  that nothing asked for). `SendMessage` is logged (`AGENT_MESSAGE` with the sender's task) and answered with a hint to file a CR.
- Dashboard (tab 1): "Cần bạn quyết" lists every owner gap, the open CRs, and `MESSAGE_THEN_EDIT` pairs: a message, then within 2 h
  another session editing a feature in a task without requirement (event logs of every worktree with an open task; evidence, not a block).
- Owner-only commands (`task approve`, `roadmap accept`, `art accept`, `tests unseal|grandfather`, `cr decide`) are refused when the
  gate is really invoked with them (also via `bash -c`, pipes, `dotnet run --project Tools/TestGate`); quoting them in a commit
  message or a grep is not running them.

```bash
./Tools/test-gate.sh task need Progress "ICoins để cộng xu" --api "interface Game.Progress.Contracts.ICoins"
./Tools/test-gate.sh cr list | cr show CR-3 | cr rule CR-3 keep "…" | cr decide (HUMAN ONLY)
```

## Working with it

```bash
./Tools/test-gate.sh roadmap status [F-ID]   # feature table, or one feature with its requirements and evidence
./Tools/test-gate.sh roadmap gaps            # what is missing, most severe first; [agent] work vs [người] decisions
./Tools/test-gate.sh roadmap next            # parallel lanes (modules don't overlap) + the exact task command
./Tools/test-gate.sh task start "F-TIMER: ..." --req R-TIMER-DEADLINE --req R-TIMER-STOP
./Tools/test-gate.sh roadmap propose "Cần lưu trạng thái quảng cáo đã xem" --feature F-AD-CONTINUE
./Tools/test-gate.sh roadmap accept R-ARENA-FEEL     # HUMAN ONLY (terminal)
```

- **Changing the plan with an AI** (`Tools/TestGate/Agent/PlanEditGate.cs`): the agent drafts the whole file in
  `.scratch/features.draft.md`, `roadmap draft <file>` checks it (ARCH110) and lists every change (`+ F-…`, `~ R-…: "old" → "new"`,
  `- R-…`). Owner and agent adjust until agreed. The agent then writes features.md with Edit/Write: PreToolUse checks the text it
  would write (invalid → blocked, the owner is not asked) and returns `ask`, so Claude Code shows the owner a yes/no with the same
  list. Yes → PostToolUse records `PLAN_EDIT_APPROVED` for that exact content; the change is in scope only while the file is that
  content. The next write asks again. Bash/scripts/MCP never ask: writing features.md through them is a scope breach.
  `Docs/Plan/Core.md` (the core idea on the Dashboard) goes through the same gate; it is free Markdown, so nothing is checked
  and the yes/no lists the changed lines (`+ …`, `- …`). An approval counts only for the file it was given for.
- **Parallel work:** one git worktree per agent. `task start` refuses modules another worktree's open task is using
  (the registry lives in the git common dir, so all worktrees see it). `roadmap next` already puts such items "after".
- **Rush:** the human tags a feature `[rush]`. Its contract dependencies may then be unfinished if their interface is frozen;
  `task start` prints the debt, the feature cannot be DONE until the provider is DONE and its tests ran again. Hard
  dependencies are never skipped.
- **The human changes the plan mid-work:** a task remembers the hash of each requirement it started with. If the human
  rewords, removes, defers or drops one, the task becomes **STALE**: Stop is blocked with the new wording, the agent adapts
  and starts a fresh task. Verified requirements whose wording changed become STALE until a covering test is updated.
- **Forgetting:** the SessionStart hook prints the roadmap summary, STALE warnings and the next 3 items, computed from
  files, not from the agent's memory.

## Story · Gameplay · Art

Three branches that move at different speeds. The rules that keep them from blocking or breaking each other:

1. **Story states experience goals, not content.** Write `R-BOSS-LEARN  ≥ 2 telegraphed attack types  [story]`, not
   "a wolf with 3 skills". Content (archetypes, skills, numbers) is data in `Logic/Domain` (`EnemyArchetypes`). Design-lint
   tests (`[Covers]` the `[story]` requirements) check every goal over the shipped content: swapping wolf → tiger needs no
   approval if the goals hold; if one breaks, the gate says which and by how much, and the human decides (content or goal).
2. **Gameplay owns timing and footprint; art meets a measurable contract.** Each feature declares its art slots in
   `Logic/Domain/<F>ArtSlots.cs` (ARCH121): size envelope, pivot, and for animated actions the marker that must land inside a
   gameplay tick window at an allowed playback speed. Animation never drives gameplay (ARCH057); it is checked against it.
3. **Graybox is generated from the contract**, so gameplay, tests and layout run from day one, and the placeholder can never
   violate what real art must meet. Real art is bound to the slot (`ArtSlotAsset`: candidate / final, ARCH120) without
   touching gameplay; presentation falls back to the graybox when nothing is bound.
4. **Art gate** (Unity EditMode, `Game.Core.Editor.Tests`, category `Art:<slot>`): measures bound art (rect/sprite/renderer
   bounds, pivot, clips and events) and checks the contract. Changing an art file runs the gate of that slot only
   (`ChangeImpact` maps files → slots through GUIDs, one prefab hop).
5. **Art level is computed:** GRAYBOX (nothing bound) → CANDIDATE (bound) → APPROVED (a human ran `art accept SLOT` for
   this content hash) → FINAL (approved, bound as final, art gate passed). Editing approved art makes it **STALE**.
6. **Plan:** `[slots: F.Name]` + `[art: approved]` on a feature → missing level is **ART_DEBT** (actor: art, shown in the
   art lane, never assigned to agents); the feature can't be DONE with art debt. `[art-risk]` + a `[feasibility]`
   requirement → gameplay work is blocked until a human accepts feasibility (decide wolf/tiger *before* coding 3 skills).

```bash
./Tools/test-gate.sh art status                 # every slot: level, bound file, art gate, measurement
./Tools/test-gate.sh art accept MathQuiz.Slime  # HUMAN ONLY: approve this exact content
./Tools/test-gate.sh roadmap next               # ...includes the art lane (artists / reviewers)
```

**Limits.** A `[Covers]` link proves a test is *meant* to check a requirement, not that it checks it well: review tests like
code. The ledger is local (`.agent/`): on a fresh clone the first green gate re-records everything, so STALE history starts
again. `UNVERIFIED` uses file times: a checkout that touches files makes requirements UNVERIFIED until the gate runs.
