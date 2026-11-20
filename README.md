# MathQuizSample: dự án Unity mẫu, kiến trúc thân thiện với AI

Game mẫu **"Quiz Defense"**: phép cộng 2 số, chọn 1 trong 3 đáp án trong 10 giây, trong lúc **quái slime ngẫu nhiên** tiến về căn cứ.
Trả lời đúng: bắn hạ con gần nhất. Sai hoặc hết giờ: quái nổi điên và sinh thêm. Hết máu: thua. Toàn bộ là **giả lập tất định** (20 tick/giây). Có thanh đếm ngược, đúng/sai/hết giờ, 10 câu một vòng,
lưu kỷ lục, chơi lại bất kỳ lúc nào.

Game chỉ là ví dụ. Thứ có giá trị là **khung kiến trúc + bộ chặn tự động**: AI có quên rule thì công cụ vẫn báo đỏ,
và việc kiểm tra **rẻ** (phần lớn tính bằng mili giây, không cần mở Unity) cũng như **ít token** (mỗi lỗi một dòng có mã rule).

## Chạy thử

```bash
dotnet test Tools/LogicTests            # 409 test: rule kiến trúc + logic + giả lập + replay, vài giây, không cần Unity
./Tools/test-gate.sh run          # gate đầy đủ (định nghĩa "xong việc")
./Tools/install-git-hooks.sh      # một lần cho mỗi clone: bật pre-commit
```

**Trong Unity** (2022.3 LTS hoặc Unity 6):
1. Unity Hub → *Add project from disk*. UniTask tự cài từ `Packages/manifest.json` (cần git).
2. Scene trống → **Tools ▸ Game ▸ Create Game Root In Scene** → **Play**. UI được dựng từ recipe nếu chưa có prefab.
3. (Khuyến nghị) **Tools ▸ Game ▸ Prefabs ▸ MathQuiz: Create Base Prefab**: tạo `QuizView.generated.prefab` và
   `MathQuizAssets.asset`. Kéo `MathQuizAssets` vào ô *Math Quiz Assets* của GameRoot. Artist làm **Prefab Variant** để skin.
4. **Tools ▸ Game ▸ Validation ▸ Validate Project**: kiểm tra toàn bộ. Nếu còn vi phạm, Unity **không cho vào Play Mode** và **build sẽ fail**.

## 8 vấn đề và cách dự án chặn

| Vấn đề | Giải pháp | Chặn bằng |
|---|---|---|
| **Prefab hay code?** | *Prefab recipe* (code dựng cấu trúc UI) → `*.generated.prefab` → artist làm Variant. Reskin = đổi Variant | Cấm sửa `.prefab/.unity/.meta` (deny), validator Unity |
| **Xung đột dữ liệu kéo thả / scene / init** | Config chỉ nằm trong ScriptableObject; state nằm trong C# thuần; reference **bắt buộc mặc định**; khởi tạo tường minh từ Bootstrap | ARCH070 (cấm config trên MonoBehaviour), ARCH081 (scene chỉ được override layout), ARCH072 (reference rỗng), ARCH043 (cấm `Find*`/singleton) |
| **Đóng gói asset theo feature** | 1 feature = 1 thư mục (code + `Assets/Private` + `Assets/Public`) + *asset entry* (`MathQuizAssets`) | ARCH080 (quét GUID: cấm tham chiếu asset private của feature khác), ARCH082/083, `test-gate.sh refs` trước khi xóa |
| **Chặn cứng để AI không quên** | Một rule engine dùng chung cho mọi tầng (xem bảng dưới) | hook, pre-commit, Play Mode, build, CI |
| **private / internal / public** | Logic **internal**; IVT chỉ cho feature của chính nó; public = Contracts + Installer/Module | **compiler (CS0122)**, ARCH020/021/022, API baseline ARCH023 |
| **partial lung tung** | Cấm partial (trừ code sinh tự động); giới hạn 300 dòng/file | ARCH030/031 |
| **Assembly + gọi hàm qua string** | Interface, typed event, serialized ref, `Animator.StringToHash` | ARCH040–044 (SendMessage, Invoke("..."), reflection, Find, Resources.Load...), ARCH001–012 (asmdef) |
| **Chặn rẻ, ít token** | Regex/YAML/asmdef chạy trong ms; lỗi = 1 dòng + link mục rule; hook im lặng khi sạch | đo được: hook **0,09 giây**, Stop hook bỏ qua trong 0,09 giây khi không có gì đổi |

### Các tầng chặn

| Tầng | Khi nào | Tác dụng |
|---|---|---|
| Hook Claude Code **sau mỗi lần sửa** | Edit/Write | quét file vừa sửa; vi phạm thì AI nhận lỗi ngay (exit 2) |
| **Compiler** | mỗi lần build | `noEngineReferences`, `internal` + IVT |
| `test-gate.sh run` / `dotnet test` | cuối task, CI | mọi rule tĩnh + reflection + API baseline + toàn bộ test |
| Hook Claude Code **Stop** | AI định kết thúc | gate đỏ thì **không cho kết thúc** (tối đa 3 lần, sau đó bắt AI báo cáo) |
| **git pre-commit** | `git commit` | quét các file đã stage (cho cả Codex/Cursor/người) |
| **Unity Editor** | import / bấm Play | báo lỗi ở Console, **hủy vào Play Mode** |
| **Build gate** | build player | `BuildFailedException` |
| **CI + CODEOWNERS** | PR | file rule/gate/baseline chỉ owner được sửa. Đây là lớp chặn cuối cùng, vì AI có shell vẫn có thể ghi file trực tiếp |

Mọi rule được giải thích trong [`Docs/RULES.md`](Docs/RULES.md) (ARCH001–ARCH095). AI chỉ đọc mục được báo.

## Tất định, tick, tách view: lõi để làm dự án lớn

| Vấn đề | Giải pháp trong dự án | Bằng chứng (test) |
|---|---|---|
| **RNG tái hiện được** | PRNG tự viết (xoshiro128\*\* + SplitMix64), **luồng RNG theo khóa** `For("arena.hop", round, enemyId, tick)` | Test vector khớp bản tham chiếu Python độc lập; bắn hạ quái A không đổi đường đi của quái B; đổi luật đáp án nhiễu không đổi phép tính |
| **Seed thôi chưa đủ** | Replay = seed + phiên bản + config + **lệnh kèm tick** + hash checkpoint mỗi giây | Chơi thật → replay khớp từng bit; sửa 1 tick trong replay thì báo đúng tick lệch đầu tiên |
| **Tick và thời gian** | Giả lập chạy theo tick số nguyên; `TickScheduler` đổi frame thành tick; hết giờ = tick hạn chót | Cùng hash ở 30/60/144 FPS và frame ngẫu nhiên; hết giờ đúng 200 tick |
| **Tách view và data** | View chỉ nhận view model, gửi input thành lệnh; `Rebind` gắn view mới giữa vòng; view tự làm mượt vị trí | Thay view giữa vòng hiển thị y hệt; view cũ được GC thu hồi |
| **Chặn cứng** | ARCH051/055/056/057 + golden replay + `SimVersion` | Thử 3 kiểu cố tình làm sai, đều bị bắt (lộ ra một lỗ hổng regex `100f`, đã vá) |
| **Chi phí** | 1 giờ chơi giả lập trong ~0,27 s | Replay test đủ rẻ để chạy ở mọi lần sửa |

Có bug: replay được **tự lưu khi lỗi** (hoặc **Tools ▸ Game ▸ Debug ▸ Save Replay**). Thả file vào `Features/MathQuiz/Tests/Replays/`
rồi chạy `dotnet test` để tái hiện mà không cần Unity.

## Theo dõi toàn hệ thống: sơ đồ sinh tự động

**[`Docs/Architecture/overview.md`](Docs/Architecture/overview.md)** cùng mỗi feature một trang trong `Docs/Architecture/Features/`.
GitHub hiển thị được Mermaid trực tiếp. Sinh lại bằng `./Tools/test-gate.sh graph --write` hoặc trong Unity: **Tools ▸ Game ▸ Regenerate System Map**.
- **L1** hệ thống: feature, Core, Bootstrap, vendor theo tầng;
- **L2** phụ thuộc giữa feature: nét liền `uses`/`wires`, nét đứt `implements`;
- **bảng feature**: public face, uses, used-by, vendor, asset, số test;
- **state machine** sinh từ bảng `Transition<TState>`, chính bảng mà code bắt buộc tuân theo;
- **bảng third-party**: tier, chỗ được phép dùng, version/hash.

| Vấn đề | Cách xử lý |
|---|---|
| Sơ đồ **lỗi thời** | Không ai vẽ tay. Gate sinh lại (vài ms) và so sánh; lệch thì báo đỏ **ARCH090**, kèm các cạnh mới/mất và lệnh sửa `./Tools/test-gate.sh graph --write`. Tài liệu viết tay bị **ARCH091** kiểm tra: tên `Type.Member`, `Game.*` và đường dẫn phải còn tồn tại |
| **Tốn token** | Đọc toàn bộ `.cs` ≈ 52.600 token; overview ≈ 600; một câu query ≈ 100. Lệnh: `graph feature <F>`, `graph impact <F>`, `graph di <F>`, `graph diff` |
| **Chuẩn hóa feature** | `./Tools/test-gate.sh new-feature Name [--contracts]` sinh feature đã đạt mọi rule (đã thử: gate xanh ngay). Không dùng `feature.yaml`: mọi thứ suy ra từ thư mục, asmdef, CODEOWNERS |
| **Plugin bên thứ 3** | `Tools/ArchitecturePolicy.txt` (được bảo vệ, là allowlist viết tay duy nhất): tier `foundation`/`presentation`/`sdk`, SDK chỉ dùng trong adapter. Vendor được ghim version/hash. Ngoại lệ phải có phạm vi, owner và hạn dùng (ARCH092–095) |

Ví dụ trong dự án: `Features/Ads` dùng **SDK giả** `Assets/ThirdParty/DemoAdsSdk` (singleton, static, callback ngoài main thread).
Chỉ `Game.Ads.Unity` (adapter) được gọi SDK; các feature khác dùng `IAdsService`.

## Test UI độc lập (view, popup, component) và cheat

**Mục tiêu:** chắc chắn nhưng không thừa. Dời 2px thì chỉ test layout của **đúng view đó**, không boot game.

| Bạn đổi | Gate chạy (`test-gate.sh plan` in ra vai trò của từng file) |
|---|---|
| 2px trong `Views/QuizView/QuizViewLayout.cs` hoặc prefab/Variant của nó | luật tĩnh + **layout gate của QuizView** (không dotnet, không PlayMode) |
| text, view model, presenter (`Logic/UseCases`) | dotnet + layout gate các view của feature |
| luật gameplay (`Logic/Simulation`, `Domain`) | dotnet (golden replay); không đụng Unity |
| composition root, installer, scene, asmdef | Unity theo đồ thị asmdef (gồm PlayMode smoke) |
| README | chỉ luật tĩnh |
| file lạ, không biết vai trò | chạy **toàn bộ** (fail-closed) |

Phân loại chính xác vì **kiến trúc làm cho nó chính xác**, không phải đoán từ diff:
- **ARCH100**: layout tĩnh chỉ nằm trong `Views/<V>/<V>Layout.cs` hoặc prefab. View chỉ được di chuyển theo dữ liệu (dòng có `// layout: dynamic`, không có hằng số).
- **ARCH101**: mỗi view có thư mục riêng, có fixture (có `smoke`) và có cases.
- **ARCH102**: DevTools chỉ có trong Editor hoặc khi có `GAME_DEVTOOLS`; bản release có define này thì build thất bại.

**Các tầng test:**
- **L0** presenter + flow (dotnet, mili-giây).
- **L1** reference của prefab.
- **L2** ràng buộc: safe area, chồng lấn, chữ bị cắt, vùng chạm. Chạy mọi fixture × 4 thiết bị.
- **L3** snapshot layout dạng text: dời 2px ra đúng 1 dòng diff, `Background/Question: 60,520 960x400 -> 60,519 960x402`. Baseline thuộc owner, sinh bằng `LAYOUT_WRITE=1`.
- **L4** PlayMode, chỉ khi đổi phần ghép nối.

**Cheat = scenario**: seed + lệnh chạy qua simulation thật (`QuizScenarios`).
- Mỗi scenario đồng thời là một cheat, một fixture (`scn-last-life`) và một replay (`ToReplay()`, `FromReplay()` mở được file bug report).
- Chỉ tạo ra được trạng thái mà người chơi thật có thể gặp.
- Xem tay bằng **Tools ▸ Game ▸ UI ▸ View Preview**: cùng fixture, cùng phép kiểm tra với gate.
- View mới: `./Tools/test-gate.sh new-view <Feature> <View>`.

## Quản lý file: không registry, không có gì phải đối soát

| Vấn đề | Cách giải (tất định, ~0 token) |
|---|---|
| AI tạo scene rác / file rác trong Assets, crash thì quên dọn | File tạm vào `.scratch/` (gitignored, Unity không import). Agent bị cấm ghi `.unity`. Hook báo ngay khi file sai chỗ (ARCH130) hoặc là rác (ARCH131). Stop chặn và **chỉ ra file rác mà chính phiên đó để lại**. Phiên crash được nhận ra từ nhật ký (có ghi file nhưng không có Stop sạch). |
| File lung tung | Đường dẫn chính là quyền sở hữu: file nằm ngoài cấu trúc đã biết bị chặn. Dời file không cần cập nhật gì. |
| File cũ, chiếm bộ nhớ | `files du` cho biết cái gì chiếm chỗ. `files clean --regenerable` chỉ xóa thứ công cụ tự tạo lại được. `files unused` liệt kê asset không ai tham chiếu (đồ thị GUID, chỉ báo cáo). |
| Xóa có làm mất việc của người không? | `files clean` chỉ xóa file **chưa vào git + agent tạo + còn nguyên từ lúc tạo** (và không phải đề xuất chờ người). Mặc định chạy thử; thêm `--yes` mới xóa thật. |
| Người dùng kéo thả file làm hệ thống sai | Không có registry nên không có gì sai. Riêng lỗi thật (dời file mà quên `.meta`, Unity sinh GUID mới) thì **ARCH132** báo đúng dòng tham chiếu và in đúng câu lệnh khôi phục `.meta`. |
| File từ AI khác, Claude cloud | Mọi thứ vào qua git nên dùng cùng bộ luật (pre-commit, CI). File không rõ nguồn gốc không bao giờ bị tự xóa. |

```bash
./Tools/test-gate.sh files                       # tóm tắt: file ngoài git, mồ côi, rác, xóa an toàn được
./Tools/test-gate.sh files clean --orphans --yes # dọn sau crash (chỉ xóa file chứng minh được là an toàn)
./Tools/test-gate.sh files unused                # asset không ai dùng
./Tools/test-gate.sh files du                    # dung lượng
```

## Story · Gameplay · Art: ba nhánh chạy song song không chặn nhau

| Vấn đề | Cách giải |
|---|---|
| Cốt truyện chốt cứng "Sói 3 skill", art không đạt thì làm lại hết | Cốt truyện ghi **ý đồ** thành requirement `[story]`, ví dụ "≥ 2 kiểu tấn công có báo trước". Sói hay Hổ là **dữ liệu** (archetype). Test design lint kiểm ý đồ trên dữ liệu: đổi nội dung mà vẫn đạt thì không cần ai duyệt; hỏng thì gate chỉ ra hỏng ý đồ nào và lệch bao nhiêu, ví dụ dơi: "first contact after 83 ticks < 100". |
| Gameplay phụ thuộc animation và kích thước world | Gameplay giữ timing (tick) và footprint. Art phải đáp ứng **hợp đồng slot đo được**: khoảng kích thước, pivot, mốc animation nằm trong cửa sổ tick ở dải tốc độ phát cho phép. Animation không bao giờ điều khiển gameplay. |
| Art chậm, thành điểm nghẽn | **Graybox sinh từ chính hợp đồng** nên luôn đạt hợp đồng; gameplay và test chạy từ ngày đầu. Art thật gắn vào slot (`ArtSlotAsset`) mà không động tới gameplay. **Art gate** đo và kiểm từng slot; sửa 1 file art thì chỉ chạy gate của slot đó. |
| Không biết art đang ở mức nào | Cấp art được **tính**: GRAYBOX → CANDIDATE → APPROVED (người `art accept`, khóa theo hash) → FINAL. Sửa art đã duyệt thì thành **STALE**. Thiếu cấp so với yêu cầu thì thành **ART_DEBT**, nằm trong làn art cho người vẽ. `[art-risk]` chặn gameplay sâu tới khi người duyệt mức khả thi. |

## Roadmap: plan là dữ liệu, trạng thái là bằng chứng

| Vấn đề | Cách giải |
|---|---|
| AI quên plan khi làm lâu | Plan nằm trong `Docs/Plan/features.md`. SessionStart nạp phần **tính ra**: tóm tắt, cảnh báo STALE, 3 việc tiếp theo. `roadmap next` cho câu lệnh `task start` cụ thể. |
| Không có danh sách tính năng để kiểm soát | Bạn quản lý 2 cấp: **Feature → Requirement**. Trạng thái tính từ test có `[Covers("R-…")]`, không ai tự gán. Tab **Tính năng** hiện "5/7 requirement đã kiểm chứng", không phần trăm đoán mò. |
| Bạn đổi plan, AI làm loạn | Plan thuộc bạn (AI bị deny, chỉ được đề xuất vào `Docs/Plan/Inbox/`). Task ghi hash của requirement lúc bắt đầu. Requirement đổi thì task **STALE**, Stop bị chặn kèm câu chữ mới. Requirement đã kiểm chứng mà bị đổi chữ cũng STALE cho tới khi test được cập nhật. |
| Rush làm sai | `hard` không bao giờ bị bỏ qua. `contract` được làm trước khi feature có `[rush]` **và** interface đã chốt; khi đó máy tự sinh **nợ tích hợp**. Feature chỉ DONE khi trả hết nợ. |
| Làm song song | Mỗi agent một worktree. Sổ đăng ký chung (git common dir) từ chối task trùng module. `roadmap next` chia làn song song. |
| Làm bù thiếu sót | `roadmap gaps`: FAILING / STALE / UNVERIFIED / NOT_RUN / NO_TEST / DEBT cho agent; NEEDS_REVIEW / PROPOSAL / UNPLANNED_CODE cho người. |

```bash
./Tools/test-gate.sh roadmap status            # bảng feature (hoặc: roadmap status F-TIMER)
./Tools/test-gate.sh roadmap next              # việc làm được ngay, theo làn song song
./Tools/test-gate.sh roadmap accept R-ARENA-FEEL   # CHỈ NGƯỜI (terminal hoặc yes/no): duyệt requirement [manual]
```
Chi tiết: `Docs/PLANNING.md`. Dashboard có thêm tab **Tính năng** và **Plan DAG**.

## Quyền và tự động hóa: Agent Flight Recorder

Agent quyết định **làm gì**. Chương trình quyết định **có được phép không** và ghi lại **chuyện gì thật sự xảy ra**. Không có node nào trong graph được tạo từ lời AI tự khai.

| Vấn đề | Cách chặn |
|---|---|
| Sửa file ngoài phạm vi | **Task = đơn vị quyền**, phạm vi lấy theo feature. AI tự mở task cho 1 feature; rộng hơn thì `task request`, rồi **người** duyệt: `task approve` trong terminal, hoặc agent chạy riêng lệnh đó và người trả lời yes/no trong Claude Code. Hook PreToolUse chặn **trước khi ghi**. |
| Lách qua Bash (`sed`…) | Chụp snapshot git trước và sau mỗi lệnh Bash, rồi xét phạm vi và luật như với Edit. Hoàn tác thì luôn được phép. |
| Fake fix test | Diff guard: bớt assert, bớt test, thêm `Ignore`/`Assume`/`#if`, xóa file test thì Stop bị chặn, trừ khi người đã duyệt `--tests`. |
| Quên quy trình, mất memory | Stop hook chạy phạm vi + test guard + gate. SessionStart nạp task, gate gần nhất và trạng thái chuỗi hash. |
| AI nói "đã xong, test xanh" | Lời khai được ghi thành CLAIM và đặt cạnh bằng chứng: file đã sửa, gate kèm hash file kết quả, vi phạm, Stop bị chặn. |
| Graph có đáng tin? | Nhật ký JSONL chỉ ghi thêm, có **chuỗi hash** (`events verify`). Graph là hình chiếu tất định của nhật ký, xóa đi dựng lại được. |

```bash
./Tools/test-gate.sh task start "Dời câu hỏi 2px" --feature MathQuiz
./Tools/test-gate.sh dash          # ghi dữ liệu Dashboard/Data/; mở Dashboard/index.html (--bundle: 1 file để gửi)
./tools/test-gate.sh serve         # http://localhost:8765, tự cập nhật
./Tools/test-gate.sh events verify # chuỗi hash còn nguyên?
./Tools/test-gate.sh task approve  # CHỈ NGƯỜI: terminal, hoặc yes/no trong Claude Code
```

## Cấu trúc

**Bố cục Root (hard rule ARCH130).** Root là thư mục có `AGENTS.md`. Ở Root chỉ có 5 thư mục (PascalCase), ba file `*.md` và các mục bắt đầu bằng dấu chấm.
Đường dẫn của tool tính từ Root (`UnityProject/Assets/...`). Đường dẫn trong Unity và trong tài liệu này tính từ `UnityProject/` (`Assets/_Game/...`).

```
AGENTS.md · CLAUDE.md · README.md   rule cho AI (ngắn; chi tiết ở Docs/RULES.md)
.claude/settings.json            hook PreToolUse/PostToolUse/Stop, quyền (--no-verify, golden/layout, lệnh chỉ dành cho người)
.github/CODEOWNERS · workflows/  owner cho file rule (thay ARCHITECTURE_OWNERS) · CI
UnityProject/                    mọi thứ Unity cần: Assets/, Packages/, ProjectSettings/ (+ Library/, Temp/, *.csproj sinh ra, bị ignore)
Tools/
├── test-gate.sh                 plan · run · check · refs · new-feature · new-view · roadmap · art · files · task · events · dash · serve · reset
├── TestGate/                    gate (build sẵn → ~0,1 giây) · claude-hook.sh: cầu nối hook của Claude Code
├── LogicTests/                  dotnet test Tools/LogicTests (UniTask NuGet)
├── GitHooks/pre-commit          + install-git-hooks.sh
├── ArchitecturePolicy.txt       vendor (tier, adapter, version/hash) + ngoại lệ có hạn dùng [bảo vệ]
└── make-template.sh · TemplateKit/   sinh Template/ (Core sạch)
Dashboard/                       index.html · app.js · style.css (viết tay) + Data/ (gate ghi ra, bị ignore) · demo.html
Docs/                            ARCHITECTURE.md · RULES.md · PLANNING.md · Architecture/ (sơ đồ SINH TỰ ĐỘNG) · Plan/ (roadmap của người)
Template/                        Core sạch không có MathQuiz/Progress, sinh bởi ./Tools/make-template.sh, bị ignore

UnityProject/Assets/ThirdParty/DemoAdsSdk/   SDK giả để minh họa ranh giới vendor
UnityProject/ProjectSettings/EditorSettings.asset   Force Text + Enter Play Mode không Domain Reload
UnityProject/Assets/_Game/
├── Core/Runtime/          public, khóa bằng PublicApi.txt: Result, IRandom, IDelay, Lifetime, [OptionalReference], [VisualTuning], [AllowStatic]
├── Core/DevTools/         ViewFixture, LayoutChecks, LayoutSnapshot (text), DeviceProfile (thuần, chỉ Editor/GAME_DEVTOOLS)
├── Core/Editor/           ViewStage, LayoutProbe, View Preview · Tests/ = layout gate cho MỌI view [bảo vệ]
├── Features/
│   ├── MathQuiz/  Logic/ (internal) · Unity/ (Installer, Module, MathQuizAssets) · Unity/Views/QuizView/ (QuizView + QuizViewLayout)
│   │              DevTools/ (Fixtures, Scenarios) · Editor/ (QuizViewPrefabRecipe, Views/QuizViewCases) · Tests/ (+ Layout/ baseline) · Assets/
│   ├── Progress/  Contracts/ (IBestScoreService + PublicApi.txt) · Logic/ (internal) · Unity/ · Tests/ · Assets/
│   └── Ads/       Contracts/ (IAdsService) · Unity/ (DemoAdsAdapter internal, AdsInstaller): adapter mẫu cho SDK
├── Bootstrap/             GameCompositionRoot (seed, replay khi lỗi), UnityGameClock/FrameSource, EventSystem · Editor/ (gates, validator, menu) · Tests/
├── Architecture/Rules/    rule engine + ArchitectureGraph (sơ đồ) dùng chung (dotnet + Unity Editor + TestGate + hook)
└── Tests/Architecture/    test rule: asmdef/source/asset, reflection, access/API baseline, fixture cho từng rule
```

## Đã kiểm chứng / chưa kiểm chứng
- ✅ `dotnet test Tools/LogicTests`: **409/409**. Mỗi rule tĩnh có fixture vi phạm và fixture hợp lệ. Có test meta bắt buộc rule mới phải kèm fixture.
- ✅ Cố tình vi phạm để thử: partial + SendMessage + Find + config int (hook và pre-commit đều chặn), sửa Contract (ARCH023 in diff),
  để Logic thành public (ARCH020), Bootstrap tham chiếu Logic (**compiler CS0122** + ARCH012).
- ✅ Hook: 0,09 giây khi sạch; Stop hook chặn khi đỏ, cho qua khi xanh, bỏ qua khi không có thay đổi; `refs` liệt kê đủ 7 tham chiếu tới Progress.
- ✅ Dashboard: dữ liệu (`Dashboard/Data/data.json`) tách khỏi giao diện tĩnh (`Dashboard/`). Ở 50k sự kiện: data.json 59 KB, bằng với lúc 5k (có test chặn); `dash` 0,96 giây (trước 3,1 giây), đầu phiên 0,77 giây (trước 1,8 giây). Có test bảo đảm giao diện chỉ đọc các trường có trong dữ liệu. Chuỗi hash được kiểm tăng dần theo điểm mốc, và nhật ký bị cắt đuôi giờ bị phát hiện.
- ✅ MCP (Unity/Blender), phiên 7 của demo:
  - tool đọc được dùng tự do; tool ghi cần task, mục tiêu ngoài phạm vi bị chặn trước;
  - tác dụng phụ ngoài phạm vi (prefab ghi thêm vào `Assets/Prefabs/`) được ghi là của agent, nên Stop bị chặn;
  - `execute_blender_code` bị chặn;
  - Unity lưu prefab muộn sau lời gọi MCP vẫn tính cho agent.
  ⚠️ Mới giả lập đúng định dạng hook, chưa thử với Unity/Blender MCP thật.
- ✅ Lệnh chỉ người được chạy (`task approve`, `roadmap accept`, `art accept`) bị từ chối trong shell của Claude Code kể cả khi đã bỏ `CLAUDECODE` và giả terminal, và trong shell của Codex (`CODEX_*`). Codex chưa có adapter hook: công việc của Codex chỉ được kiểm ở pre-commit/CI.
- ✅ Người sửa tay không làm agent kẹt (kiểm trên bản git copy):
  - H1 gõ sai plan, H2 file đặt sai chỗ, H3 đổi gameplay làm golden đỏ, H4 người xóa assert: Stop của agent cho qua (exit 0) và liệt kê vào mục "CHỜ NGƯỜI", kèm lệnh cho người (lỡ tay thì `git checkout`, cố ý thì tăng SimVersion + `GOLDEN_WRITE=1`).
  - Đối chứng: agent gây lỗi build hoặc agent bớt assert thì vẫn bị chặn (exit 2).
  - Sửa trong inspector `QuizConfigAsset` mà phá ý đồ `[story]` thì design-lint bắt như sửa code.
  - File mồ côi chỉ tính khi có bằng chứng phiên đã kết thúc; khóa task của worktree đã mất tự được dọn.
- ✅ Quản lý file. Trên repo git thật:
  - Không báo nhầm cho dự án sạch.
  - 8 kiểu file rác đều bị bắt; `.scratch/` và `Scenes/` không bị báo.
  - Kịch bản Wolf.fbx bị dời mà quên `.meta`: báo đúng prefab, đúng dòng và lệnh sửa; chạy lệnh đó xong thì hết lỗi. Xóa hẳn thì in lệnh khôi phục.
  - Phiên 5 của demo:
    - hook báo ngay khi có scene rác;
    - phiên crash để lại 2 file mồ côi;
    - `files clean --orphans --yes` xóa đúng 2 file đó, giữ file người đã sửa, file không rõ nguồn gốc và đề xuất trong inbox.
- ✅ Story · Gameplay · Art: slime/dơi là archetype dữ liệu; replay và golden không đổi. Design lint đạt với slime và chỉ ra chính xác ý đồ mà dơi làm hỏng. Graybox luôn đạt hợp đồng của nó. Phiên 4 của demo:
  - gắn art ứng viên → CANDIDATE, và `plan` chỉ chạy art gate của MathQuiz.Slime;
  - agent tự `art accept` bị chặn;
  - người duyệt → APPROVED;
  - người vẽ sửa png → STALE.
  25 assembly biên dịch được với stub Unity. **Art gate chưa chạy trong Unity thật.**
- ✅ Roadmap: plan mẫu của Quiz Defense có 10 feature và 27 requirement; 18 requirement được gắn với 36 test có sẵn bằng `[Covers]`. Sau khi chạy gate: 4 feature DONE.
  - F-ARENA chờ người duyệt R-ARENA-FEEL, nên F-REPLAY bị chặn vì phụ thuộc hard.
  - F-AD-CONTINUE đang rush, có 1 khoản nợ tích hợp với F-ADS.
  - Đã chạy kịch bản qua hook:
    - task phụ thuộc F-ARENA chưa xong bị từ chối;
    - task rush được phép và in ra khoản nợ;
    - người sửa câu chữ R-AD-OFFER giữa chừng thì Stop chặn với nhãn STALE, còn lần sửa đó được ghi là "không quan sát được", không quy cho agent.
  - Hai worktree không lấy được cùng module (test trên repo thật).
- ✅ Flight Recorder: chạy kịch bản end-to-end với **đúng JSON mà Claude Code gửi vào hook**, gồm 2 phiên và 1 subagent. Kết quả:
  - Chặn sửa khi chưa có task, chặn ghi ngoài phạm vi.
  - Bắt được lần sửa Bootstrap bằng `sed`; lần hoàn tác sau đó được ghi là hợp lệ.
  - Bắt được README do người sửa ngoài phiên.
  - Chặn Stop khi xóa 1 assertion; lời khai "mọi test đều xanh" bị chấm "mâu thuẫn".
  - Agent tự `task approve` bị từ chối.
  - Sửa, xóa hay đổi thứ tự một dòng nhật ký đều bị `verify` chỉ ra.
  - Dashboard chạy trên Chromium (sáng, tối, điện thoại): không lỗi JS, không tràn ngang.
- ✅ Test UI: `plan` cho 6 kiểu thay đổi (2px, text, gameplay, composition root, README, file lạ) ra đúng như bảng trên. Mọi scenario
  đều tất định và là replay hợp lệ. Mọi fixture render được trên view giả. `LayoutChecks`/`LayoutSnapshot` có test thuần.
  `new-view` sinh view đạt mọi luật ngay, biên dịch được với stub Unity.
- ✅ **23 assembly được biên dịch riêng từng cái** (đúng như asmdef, gồm cả Ads + vendor giả) với UnityEngine core 2021.3 + UniTask. Phần uGUI/Editor dùng stub.
  `UniTask.SwitchToMainThread` trong adapter chỉ có ở bản UniTask cho Unity, nên chưa biên dịch được ở đây.
- ✅ Sơ đồ và vendor: MathQuiz gọi thẳng SDK (bị ARCH094 + ARCH002 + ARCH090 bắt), sửa code vendor (ARCH093 hash), tài liệu nhắc method
  đã đổi tên (ARCH091), `graph diff` liệt kê cạnh mới, `new-feature Currency --contracts` đạt gate ngay.
- ✅ Graph không thể lệch im lặng: cú pháp generator không đọc được thì báo đỏ (kể cả sau `graph --write`); một test reflection
  đối chiếu bảng transition lúc chạy với sơ đồ (đã thử giả lập bug generator, test bắt được).
- ✅ Quy mô lớn (150 feature, 300 MB SDK): hook 92 ms; `check` 1 file 0,56 s (cache hash vendor, CI vẫn băm toàn bộ);
  overview ≈ 4.700 token (trước đây 16.000).
- ✅ Unity **không chặn** Play Mode/build chỉ vì sơ đồ cũ: chỉ cảnh báo và có menu sinh lại.
- ⚠️ Flight Recorder **chưa được chạy với một phiên Claude Code thật**: đã giả lập đúng định dạng JSON của hook, nhưng một số trường (`agent_type`, transcript) có thể khác theo phiên bản; thiếu thì chỉ mất nhãn, không gây lỗi. Nhật ký **chống sửa im lặng**, không chống được ghi đè: agent cùng user vẫn có thể xóa cả file hoặc cắt phần cuối (đã có test nêu rõ giới hạn này). Trọng tài cuối là CI. Adapter cho Codex chưa làm.
- ⚠️ **Layout gate (L2/L3) và View Preview chưa chạy trong Unity thật** (chỉ mới biên dịch với stub). Lần chạy đầu:
  owner chạy `LAYOUT_WRITE=1 ./Tools/run-unity-tests.sh EditMode "View:MathQuiz.QuizView"` để tạo baseline, rồi commit
  `Tests/Layout/QuizView.layout.txt`. Nếu L2 báo lỗi thật (ví dụ chữ bị cắt trên tablet), sửa recipe. Nếu đo chữ sai khi chạy
  `-nographics`, bỏ cờ đó cho lượt chạy layout.
- ⚠️ **Chưa chạy trong Unity Editor thật**: chưa kiểm chứng Play Mode gate, build gate, import validator, recipe prefab,
  EditMode/PlayMode tests, `UnityGameClock`/`UnityFrameSource` (UniTask bản Unity), view arena, `JsonUtility` với class `internal`. Nếu lỗi, gửi log Console.
- ⚠️ `.github/CODEOWNERS` cần thay `ARCHITECTURE_OWNERS` bằng user/team thật và bật branch protection.
