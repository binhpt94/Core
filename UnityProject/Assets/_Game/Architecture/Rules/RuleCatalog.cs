using System.Collections.Generic;

namespace Game.Architecture.Rules
{
    /// <summary>One broken rule. ToString() is the single-line, token-cheap format every gate prints.</summary>
    public sealed class RuleViolation
    {
        public string RuleId { get; }
        public string Path { get; }
        public int Line { get; }
        public string Message { get; }

        public RuleViolation(string ruleId, string path, int line, string message)
        {
            RuleId = ruleId;
            Path = path;
            Line = line;
            Message = message;
        }

        public override string ToString() =>
            RuleId + " " + Path + (Line > 0 ? ":" + Line : "") + " " + Message;
    }

    public sealed class RuleInfo
    {
        public string Id { get; }
        public string Title { get; }

        /// <summary>true = checked statically by <see cref="ArchitectureScanner"/> (no compiled code needed).</summary>
        public bool IsStatic { get; }

        public RuleInfo(string id, string title, bool isStatic)
        {
            Id = id;
            Title = title;
            IsStatic = isStatic;
        }
    }

    /// <summary>
    /// Every rule ID with a one-line title. Details and fixes: Docs/RULES.md#archxxx (kept in sync by a test).
    /// Static rules run in milliseconds without Unity (hooks, pre-commit, TestGate, Play Mode gate).
    /// Reflection rules need compiled code (dotnet test / Unity EditMode). Unity rules need the Editor.
    /// </summary>
    public static class RuleCatalog
    {
        public static IReadOnlyList<RuleInfo> All { get; } = new List<RuleInfo>
        {
            // Assemblies (.asmdef)
            new RuleInfo("ARCH001", "Pure assembly must set noEngineReferences", true),
            new RuleInfo("ARCH002", "Pure assembly may reference only Game.* and UniTask", true),
            new RuleInfo("ARCH003", "Reference asmdefs by name, not GUID", true),
            new RuleInfo("ARCH004", "Assembly name must follow its folder", true),
            new RuleInfo("ARCH005", "Contracts may reference only Game.Core", true),
            new RuleInfo("ARCH006", "Core/Logic must not reference Unity-side assemblies", true),
            new RuleInfo("ARCH007", "Cross-feature references must target *.Contracts", true),
            new RuleInfo("ARCH008", "Only Bootstrap and tests may reference another feature's Unity/Editor layer", true),
            new RuleInfo("ARCH009", "No circular assembly references", true),
            new RuleInfo("ARCH010", "Every feature has README.md and a Tests assembly", true),
            new RuleInfo("ARCH011", "autoReferenced=false; overrideReferences only in tests", true),
            new RuleInfo("ARCH012", "Bootstrap must not reference feature Logic assemblies", true),
            new RuleInfo("ARCH013", "Architecture/ and Tests/Architecture/ assemblies are Editor-only (never in a build)", true),

            // Access control
            new RuleInfo("ARCH020", "Logic types must be internal", false),
            new RuleInfo("ARCH021", "InternalsVisibleTo only to the same feature", true),
            new RuleInfo("ARCH022", "No public mutable fields on public types", false),
            new RuleInfo("ARCH023", "Public API changed without updating PublicApi.txt", false),

            // Code shape
            new RuleInfo("ARCH030", "No partial types in hand-written code", true),
            new RuleInfo("ARCH031", "File too long (split responsibilities)", true),
            new RuleInfo("ARCH033", "Code nothing references (Roslyn, TestGate only: changed files)", false),

            // Hidden invocation / lookup by string
            new RuleInfo("ARCH040", "No Unity string dispatch (SendMessage, Invoke(\"..\"), StartCoroutine(\"..\"))", true),
            new RuleInfo("ARCH041", "No reflection or dynamic invocation in game code", true),
            new RuleInfo("ARCH042", "No lookup by name (GameObject.Find, Transform.Find, Resources.Load, Shader.Find)", true),
            new RuleInfo("ARCH043", "No scene search or singletons outside Bootstrap", true),
            new RuleInfo("ARCH044", "No Animator parameters by string (use cached hashes)", true),

            // Pure code
            new RuleInfo("ARCH050", "No UnityEngine/UnityEditor in pure code", true),
            new RuleInfo("ARCH051", "No System.Random/DateTime.Now/Guid.NewGuid in Logic", true),
            new RuleInfo("ARCH052", "Pure code must not wait directly (use IDelay)", true),
            new RuleInfo("ARCH053", ".Forget() only inside Lifetime", true),
            new RuleInfo("ARCH054", "Namespace segment shadows a Unity type", true),
            new RuleInfo("ARCH055", "No GetHashCode() in pure code (differs per process/runtime)", true),
            new RuleInfo("ARCH056", "No floating point in Domain/Simulation", true),
            new RuleInfo("ARCH057", "MonoBehaviours must not see Domain/Simulation", true),

            // State & async (reflection)
            new RuleInfo("ARCH060", "No mutable static state", false),
            new RuleInfo("ARCH061", "No static events", false),
            new RuleInfo("ARCH062", "No async void / async UniTaskVoid", false),

            // Unity data ownership
            new RuleInfo("ARCH070", "No serialized config values on feature MonoBehaviours", true),
            new RuleInfo("ARCH071", "ScriptableObjects expose no public mutable state", true),
            new RuleInfo("ARCH072", "Serialized reference is missing (required by default)", false),
            new RuleInfo("ARCH073", "Missing script in prefab/scene/asset", true),

            // Assets
            new RuleInfo("ARCH080", "Cross-feature reference to a non-Public asset", true),
            new RuleInfo("ARCH081", "Scene overrides a prefab property outside the layout allowlist", true),
            new RuleInfo("ARCH082", "Feature asset outside Assets/Public or Assets/Private", true),
            new RuleInfo("ARCH083", "No Resources folders in game code", true),
            new RuleInfo("ARCH084", "Force Text serialization and a .meta for every asset", true),

            // UI testability & dev tools
            new RuleInfo("ARCH100", "Static layout only in the layout recipe or prefab", true),
            new RuleInfo("ARCH101", "Every view has its folder, fixtures (incl. smoke) and view cases", true),
            new RuleInfo("ARCH102", "Dev tools are constrained to Editor/GAME_DEVTOOLS and never referenced by runtime code", true),

            // Product plan
            new RuleInfo("ARCH110", "Docs/Plan/features.md is invalid or inconsistent", true),
            new RuleInfo("ARCH111", "[Covers] names a requirement that is not in the plan", true),

            // Art slots
            new RuleInfo("ARCH120", "Art binding names an unknown slot or another feature's slot", true),
            new RuleInfo("ARCH121", "Art slot contracts: id 'Feature.Name', unique, declared in Logic/Domain/*ArtSlots.cs", true),

            // Files
            new RuleInfo("ARCH130", "File outside the known layout", true),
            new RuleInfo("ARCH131", "Leftover/junk file (temp, copy, untitled, scene outside Scenes/)", true),
            new RuleInfo("ARCH132", "Reference to a GUID that no longer exists (moved/deleted without its .meta)", true),

            // System map & third-party
            new RuleInfo("ARCH090", "Generated architecture overview is stale", true),
            new RuleInfo("ARCH091", "Doc mentions a code name or path that no longer exists", true),
            new RuleInfo("ARCH092", "Vendor DLL is Auto Referenced", true),
            new RuleInfo("ARCH093", "Third-party code not registered, not pinned, or modified", true),
            new RuleInfo("ARCH094", "Third-party used outside its allowed tier/adapter", true),
            new RuleInfo("ARCH095", "Architecture exception invalid or expired", true),
        };

        /// <summary>
        /// Documentation rules (generated system map, doc anchors). Enforced by hooks, pre-commit, the gate and CI,
        /// but NOT by the Unity Play Mode / import / build gates: a stale diagram must never stop someone from
        /// playing or building the game. Unity only warns and offers Tools > Game > Regenerate System Map.
        /// </summary>
        public static bool IsDocumentationRule(string ruleId) => ruleId == "ARCH090" || ruleId == "ARCH091";

        public static string DocsLink(string ruleId) => "Docs/RULES.md#" + ruleId.ToLowerInvariant();
    }
}
