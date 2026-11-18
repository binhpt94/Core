using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Game.Architecture.Rules;
using NUnit.Framework;

namespace Game.Architecture.Tests
{
    /// <summary>
    /// Regression tests for the system-map generator: it must never silently produce a wrong diagram.
    /// </summary>
    public sealed class GraphTests
    {
        private string _tmp;

        [SetUp]
        public void SetUp()
        {
            _tmp = Path.Combine(Path.GetTempPath(), "graph-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmp);
            Write("UnityProject/ProjectSettings/EditorSettings.asset", "m_SerializationMode: 2\n");
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("GAME_ARCH_NO_CACHE", null);
            if (Directory.Exists(_tmp))
                Directory.Delete(_tmp, true);
        }

        private void Write(string rel, string content)
        {
            string full = Path.Combine(_tmp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        private void Asmdef(string rel, string name, string refs, bool pure, string precompiled = "")
        {
            Write(rel, "{ \"name\": \"" + name + "\", \"references\": [" + refs + "], \"precompiledReferences\": [" + precompiled + "], " +
                       "\"autoReferenced\": false, \"overrideReferences\": " + (precompiled.Length > 0 ? "true" : "false") + ", \"noEngineReferences\": " + (pure ? "true" : "false") + " }");
        }

        private const string Enum = "namespace Game.A.UseCases { internal enum S { Idle, Run, Done } }\n";

        private ArchitectureGraph Table(string rows) =>
            BuildWith("UnityProject/Assets/_Game/Features/A/Logic/UseCases/M.cs", Enum +
                "namespace Game.A.UseCases { internal sealed class M {\n" +
                "  public const string Go = \"Go\";\n" +
                "  internal static IReadOnlyList<Transition<S>> Transitions { get; } = new Transition<S>[]\n  {\n" + rows + "\n  };\n} }\n");

        private ArchitectureGraph BuildWith(string rel, string code)
        {
            Write(rel, code);
            return ArchitectureGraph.Build(_tmp);
        }

        // ---------- fix 1: fail-closed state tables ----------

        [Test]
        public void StateTable_TargetTypedNew_IsParsed()
        {
            var g = Table("new Transition<S>(S.Idle, Go, S.Run),\n new(S.Run, \"Stop\", S.Done),");

            Assert.That(g.ParseErrors, Is.Empty, string.Join("\n", g.ParseErrors));
            var rows = g.StateMachines.Single().Transitions;
            Assert.That(rows, Is.EqualTo(new[] { ("Idle", "Go", "Run"), ("Run", "Stop", "Done") }));
        }

        [TestCase("new Transition<S>(S.Idle, Go, S.Run),\n Make(S.Run),")]                    // row it can't read
        [TestCase("new Transition<S>(S.Idle, Go, S.Run),\n new(S.Run, Triggers.Stop, S.Done),")] // trigger not a local const
        [TestCase("new Transition<S>(S.Idle, Go, S.Run),\n new Transition<S>(S.Run, Go, Other.Done),")] // wrong enum
        public void StateTable_UnreadableRow_IsAnError_NotSilentlyDropped(string rows)
        {
            var g = Table(rows);

            Assert.That(g.ParseErrors.Select(e => e.RuleId), Is.EqualTo(new[] { "ARCH090" }), string.Join("\n", g.ParseErrors));
        }

        [Test]
        public void Transition_OutsideARecognisedTable_IsAnError()
        {
            var g = BuildWith("UnityProject/Assets/_Game/Features/A/Logic/UseCases/M.cs", Enum +
                "namespace Game.A.UseCases { internal sealed class M { void F() { var t = new Transition<S>(S.Idle, \"Go\", S.Run); } } }\n");

            Assert.That(g.ParseErrors.Single().Message, Does.Contain("outside a recognised table"));
        }

        /// <summary>Backstop: every real runtime table equals what the generator parsed (same rows, same order).</summary>
        [Test]
        public void RuntimeTables_MatchTheDiagram()
        {
            string root = SourceText.FindRepoRoot(TestContext.CurrentContext.TestDirectory);
            var graph = ArchitectureGraph.Build(root);
            Assert.That(graph.ParseErrors, Is.Empty, string.Join("\n", graph.ParseErrors));

            int checkedTables = 0;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var type in GameTypeScanner.GameTypes())
            {
                var members = type.GetProperties(flags).Select(p => (p.Name, Type: p.PropertyType, Get: (Func<object>)(() => p.GetValue(null))))
                    .Concat(type.GetFields(flags).Where(f => !f.Name.Contains("<")).Select(f => (f.Name, Type: f.FieldType, Get: (Func<object>)(() => f.GetValue(null)))));
                foreach (var m in members.Where(x => IsTransitionTable(x.Type)))
                {
                    var runtime = ((IEnumerable)m.Get()).Cast<object>().Select(Row).ToList();
                    var parsed = graph.StateMachines.SingleOrDefault(s => s.Owner == type.Name && s.Table == m.Name);
                    Assert.That(parsed, Is.Not.Null, type.Name + "." + m.Name + " exists at runtime but is missing from the diagram");
                    Assert.That(parsed.Transitions.Select(t => t.From + "|" + t.Trigger + "|" + t.To), Is.EqualTo(runtime),
                        type.Name + "." + m.Name + ": diagram differs from the runtime table");
                    checkedTables++;
                }
            }
            Assert.That(checkedTables, Is.EqualTo(graph.StateMachines.Count), "diagram contains tables that don't exist at runtime");
            Assert.That(checkedTables, Is.GreaterThan(0), "expected at least QuizSession.Transitions");
        }

        private static bool IsTransitionTable(Type t)
        {
            var element = t.IsArray ? t.GetElementType()
                : t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? t.GetGenericArguments()[0] : null;
            return element != null && element.IsGenericType && element.GetGenericTypeDefinition().FullName == "Game.Core.Transition`1";
        }

        private static string Row(object t)
        {
            var type = t.GetType();
            return type.GetProperty("From").GetValue(t) + "|" + type.GetProperty("Trigger").GetValue(t) + "|" + type.GetProperty("To").GetValue(t);
        }

        // ---------- fix 2: precompiled vendor DLLs and indirect implements ----------

        [Test]
        public void PrecompiledVendorDll_AppearsInTheGraph()
        {
            Write(ArchitecturePolicy.FileName, "vendor id=ads tier=sdk assemblies=AdsSdk adapter=Game.A.Unity source=Assets/ThirdParty/Ads sha256=x\n");
            Asmdef("UnityProject/Assets/_Game/Features/A/Unity/Game.A.Unity.asmdef", "Game.A.Unity", "", false, precompiled: "\"AdsSdk.dll\"");

            var g = ArchitectureGraph.Build(_tmp);

            Assert.That(g.VendorUsers["ads"], Does.Contain("A"));
            Assert.That(g.Features["A"].Vendors, Does.Contain("ads"));
        }

        [Test]
        public void ImplementsThroughBaseClass_IsAnEdge()
        {
            Write("UnityProject/Assets/_Game/Features/B/Contracts/IScore.cs", "namespace Game.B.Contracts { public interface IScore { } }");
            Write("UnityProject/Assets/_Game/Features/A/Logic/Base.cs", "namespace Game.A { internal abstract class ScoreBase : Game.B.Contracts.IScore { } }");
            Write("UnityProject/Assets/_Game/Features/A/Logic/Impl.cs", "namespace Game.A { internal sealed class Score : ScoreBase { } }");
            Asmdef("UnityProject/Assets/_Game/Features/A/Logic/Game.A.asmdef", "Game.A", "\"Game.B.Contracts\"", true);
            Asmdef("UnityProject/Assets/_Game/Features/B/Contracts/Game.B.Contracts.asmdef", "Game.B.Contracts", "", true);

            var g = ArchitectureGraph.Build(_tmp);

            Assert.That(g.Edges.Select(e => e.ToString()), Does.Contain("A implements B.Contracts"));
        }

        // ---------- fix 4: docs stay small at scale ----------

        [Test]
        public void Overview_SummarisesWhenTooManyEdges_AndEveryFeatureGetsAPage()
        {
            for (int i = 0; i < 4; i++)
                Asmdef("UnityProject/Assets/_Game/Features/F" + i + "/Logic/Game.F" + i + ".asmdef", "Game.F" + i, i > 0 ? "\"Game.F0\"" : "", true);
            var g = ArchitectureGraph.Build(_tmp);

            var docs = ArchitectureDocs.Render(g, maxOverviewEdges: 1);

            Assert.That(docs[ArchitectureDocs.OverviewPath], Does.Contain("too many for one readable diagram").And.Contain("Most depended-on"));
            Assert.That(docs[ArchitectureDocs.OverviewPath], Does.Not.Contain("-->|uses|"));
            Assert.That(docs.Keys, Does.Contain(ArchitectureDocs.FeaturePagePath("F3")));
            Assert.That(docs[ArchitectureDocs.FeaturePagePath("F3")], Does.Contain("F3[F3] -->|uses| F0[F0]"));
        }

        [Test]
        public void OrphanFeaturePage_IsStale()
        {
            Asmdef("UnityProject/Assets/_Game/Features/A/Logic/Game.A.asmdef", "Game.A", "", true);
            ArchitectureDocs.WriteAll(_tmp);
            Write(ArchitectureDocs.FeaturePagePath("Deleted"), "old");

            var stale = new DocRules(_tmp).CheckGenerated().ToList();

            Assert.That(stale.Any(v => v.Path.EndsWith("Deleted.md") && v.Message.Contains("no longer exists")), Is.True, string.Join("\n", stale));
            ArchitectureDocs.WriteAll(_tmp);
            Assert.That(new DocRules(_tmp).CheckGenerated(), Is.Empty, "WriteAll deletes orphans");
        }

        // ---------- fix 3: vendor hash cache ----------

        [Test]
        public void VendorHashCache_ReusesUntilSizeOrTimeChanges_AndCanBeDisabled()
        {
            // CI runners set CI=true, which turns the cache off; this test checks the cache itself.
            string savedCi = Environment.GetEnvironmentVariable("CI");
            string savedNoCache = Environment.GetEnvironmentVariable("GAME_ARCH_NO_CACHE");
            Environment.SetEnvironmentVariable("CI", null);
            Environment.SetEnvironmentVariable("GAME_ARCH_NO_CACHE", null);
            try
            {
                string dir = Path.Combine(_tmp, "UnityProject", "Assets", "ThirdParty", "V");
                Write("UnityProject/Assets/ThirdParty/V/lib.so", "AAAA");
                string first = ThirdPartyRules.HashFolderCached(_tmp, dir);
                Assert.That(File.Exists(Path.Combine(_tmp, ThirdPartyRules.CacheFile)), Is.True);

                // Same size + same mtime: cache hit (this is why CI never uses the cache)
                var file = Path.Combine(dir, "lib.so");
                var time = File.GetLastWriteTimeUtc(file);
                File.WriteAllText(file, "BBBB");
                File.SetLastWriteTimeUtc(file, time);
                Assert.That(ThirdPartyRules.HashFolderCached(_tmp, dir), Is.EqualTo(first));

                Environment.SetEnvironmentVariable("GAME_ARCH_NO_CACHE", "1");
                Assert.That(ThirdPartyRules.HashFolderCached(_tmp, dir), Is.Not.EqualTo(first), "no-cache mode always reads the content");
                Environment.SetEnvironmentVariable("GAME_ARCH_NO_CACHE", null);

                File.WriteAllText(file, "CCCCC"); // size changes
                Assert.That(ThirdPartyRules.HashFolderCached(_tmp, dir), Is.EqualTo(ThirdPartyRules.HashFolder(dir)));
            }
            finally
            {
                Environment.SetEnvironmentVariable("CI", savedCi);
                Environment.SetEnvironmentVariable("GAME_ARCH_NO_CACHE", savedNoCache);
            }
        }

        // ---------- fix 5: documentation rules never block Unity ----------

        [Test]
        public void DocumentationRules_AreExactlyTheDocRules()
        {
            var doc = RuleCatalog.All.Where(r => RuleCatalog.IsDocumentationRule(r.Id)).Select(r => r.Id);
            Assert.That(doc, Is.EquivalentTo(new[] { "ARCH090", "ARCH091" }));
        }
    }
}
