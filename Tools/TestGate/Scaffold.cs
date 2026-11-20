using System;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// `test-gate.sh new-feature Name [--contracts]`: creates a feature that already satisfies every rule
/// (folders, asmdefs, internal Logic + InternalsVisibleTo, public Installer/Module, README, tests,
/// Assets/Public|Private, PublicApi.txt), then regenerates the overview. Agents don't have to remember the layout.
/// </summary>
internal static class Scaffold
{
    public static int NewFeature(string root, string[] args)
    {
        string name = args.Length > 1 ? args[1] : null;
        bool contracts = Array.IndexOf(args, "--contracts") >= 0;
        if (name == null || !Regex.IsMatch(name, "^[A-Z][A-Za-z0-9]+$"))
        {
            Console.Error.WriteLine("Usage: new-feature <PascalCaseName> [--contracts]");
            return 64;
        }

        string dir = Path.Combine(root, "UnityProject", "Assets", "_Game", "Features", name);
        if (Directory.Exists(dir))
        {
            Console.Error.WriteLine("Feature already exists: " + dir);
            return 1;
        }

        string svc = name + "Service";
        string logicRefs = "\"UniTask\", \"Game.Core\"" + (contracts ? ", \"Game." + name + ".Contracts\"" : "");

        if (contracts)
        {
            Write(dir, "Contracts/Game." + name + ".Contracts.asmdef", Asmdef("Game." + name + ".Contracts", "\"UniTask\", \"Game.Core\"", pure: true));
            Write(dir, "Contracts/I" + svc + ".cs", $@"namespace Game.{name}.Contracts
{{
    /// <summary>Public API of the {name} feature for other features. Changing it updates PublicApi.txt (code owners).</summary>
    public interface I{svc}
    {{
    }}
}}
");
            Write(dir, "Contracts/PublicApi.txt", "interface Game." + name + ".Contracts.I" + svc + "\n");
        }

        Write(dir, "Logic/Game." + name + ".asmdef", Asmdef("Game." + name, logicRefs, pure: true));
        Write(dir, "Logic/AssemblyInfo.cs", $@"using System.Runtime.CompilerServices;

// Logic is feature-private (ARCH020/ARCH021).
[assembly: InternalsVisibleTo(""Game.{name}.Unity"")]
[assembly: InternalsVisibleTo(""Game.{name}.Tests"")]
");
        Write(dir, "Logic/UseCases/" + svc + ".cs", $@"{(contracts ? "using Game." + name + ".Contracts;\n\n" : "")}namespace Game.{name}.UseCases
{{
    /// <summary>Entry point of the {name} logic. Pure C#, constructor-injected, no side effects in the constructor.</summary>
    internal sealed class {svc}{(contracts ? " : I" + svc : "")}
    {{
    }}
}}
");

        Write(dir, "Unity/Game." + name + ".Unity.asmdef", Asmdef("Game." + name + ".Unity",
            "\"UniTask\", \"Game.Core\", \"Game." + name + "\"" + (contracts ? ", \"Game." + name + ".Contracts\"" : ""), pure: false));
        Write(dir, "Unity/" + name + "Installer.cs", $@"using Game.Core;
using Game.{name}.UseCases;

namespace Game.{name}.Presentation
{{
    /// <summary>Public face of {name} for the composition root. Everything else stays internal.</summary>
    public sealed class {name}Module
    {{
        private readonly {svc} _service;

        internal {name}Module({svc} service) => _service = service;
    }}

    public static class {name}Installer
    {{
        public static {name}Module Install(Lifetime lifetime, IGameLogger logger) => new {name}Module(new {svc}());
    }}
}}
");

        Write(dir, "Tests/Game." + name + ".Tests.asmdef", $@"{{
    ""name"": ""Game.{name}.Tests"",
    ""rootNamespace"": ""Game.{name}.Tests"",
    ""references"": [ ""UniTask"", ""Game.Core"", ""Game.{name}""{(contracts ? ", \"Game." + name + ".Contracts\"" : "")}, ""UnityEngine.TestRunner"", ""UnityEditor.TestRunner"" ],
    ""includePlatforms"": [ ""Editor"" ],
    ""excludePlatforms"": [],
    ""allowUnsafeCode"": false,
    ""overrideReferences"": true,
    ""precompiledReferences"": [ ""nunit.framework.dll"" ],
    ""autoReferenced"": false,
    ""defineConstraints"": [ ""UNITY_INCLUDE_TESTS"" ],
    ""versionDefines"": [],
    ""noEngineReferences"": true
}}
");
        Write(dir, "Tests/" + svc + "Tests.cs", $@"using Game.{name}.UseCases;
using NUnit.Framework;

namespace Game.{name}.Tests
{{
    public sealed class {svc}Tests
    {{
        [Test]
        public void CanBeCreated()
        {{
            Assert.That(new {svc}(), Is.Not.Null);
        }}
    }}
}}
");
        Write(dir, "Logic/Domain/" + name + "ArtSlots.cs", $@"using System.Collections.Generic;
using Game.Core;

namespace Game.{name}.Domain
{{
    /// <summary>
    /// Art slots of {name}: the measurable promise art must keep (size envelope, pivot, timed actions, intent). Gameplay uses
    /// the slot id only; the graybox is generated from the contract. Standard art process (Docs/PLANNING.md): declare a slot
    /// here, show it in a view (its Cases.ArtPlacements), then `art brief` → Claude draws a candidate (`art add`) → owner judges
    /// on the compare page (`art note`) → Codex final (`art codex`) → `art accept`. Add slots as static properties, e.g.
    /// Hero = new ArtSlotContract(HeroId, ArtSlotKind.UiImage, ..., intent: ""what it is for"") and list them in Slots.
    /// </summary>
    internal sealed class {name}ArtSlots : IArtSlotCatalog
    {{
        public IReadOnlyList<ArtSlotContract> Slots => new ArtSlotContract[0];
    }}
}}
");
        Write(dir, "Assets/Public/.gitkeep", "");
        Write(dir, "Assets/Private/.gitkeep", "");
        Write(dir, "Assets/Private/Art/_feature.style.md", $@"# {name} style lock

<!-- Everything except this title and one-line comments is pasted into every art brief and AI prompt of {name}.
     Write the look once (palette, mood, outline, references); per-slot notes go to Art/<Name>/<Name>.style.md (`art note`). -->

- Look: (e.g. flat 2D cartoon, thick dark outline)
- Colours: (palette, contrast against the background)
- Readability: (how big it shows on a phone; what must read at a glance)
");
        Write(dir, "README.md", $@"# Feature: {name}

## Responsibility
- (what this feature owns)

## NOT responsible for
- (what it deliberately leaves to others)

## Public face (everything else is `internal`)
| Type | Assembly | Use |
|---|---|---|
| `{name}Installer.Install(...)` → `{name}Module` | Game.{name}.Unity | Bootstrap only |
{(contracts ? "| `I" + svc + "` | Game." + name + ".Contracts | other features (locked by `Contracts/PublicApi.txt`) |\n" : "")}
## Invariants (each has a test)
- (rules that must always hold)

## Tests
`dotnet test Tools/LogicTests --filter FullyQualifiedName~Game.{name}`
");

        Console.WriteLine("created Assets/_Game/Features/" + name + (contracts ? " (with Contracts)" : ""));
        Console.WriteLine("next: wire " + name + "Installer in Bootstrap if needed, then ./Tools/test-gate.sh graph --write && ./Tools/test-gate.sh run");
        return 0;
    }

    private static string Asmdef(string name, string refs, bool pure) => $@"{{
    ""name"": ""{name}"",
    ""rootNamespace"": ""{name}"",
    ""references"": [ {refs} ],
    ""includePlatforms"": [],
    ""excludePlatforms"": [],
    ""allowUnsafeCode"": false,
    ""overrideReferences"": false,
    ""precompiledReferences"": [],
    ""autoReferenced"": false,
    ""defineConstraints"": [],
    ""versionDefines"": [],
    ""noEngineReferences"": {(pure ? "true" : "false")}
}}
";

    private static void Write(string dir, string rel, string content)
    {
        string path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, content);
    }
}
