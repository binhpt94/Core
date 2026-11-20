using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Game.Architecture.Rules;
using NUnit.Framework;

namespace Game.Architecture.Tests
{
    /// <summary>
    /// Access-control rules on compiled types:
    /// ARCH020 Logic is internal · ARCH022 no public mutable fields · ARCH023 public API baseline.
    /// The compiler is the real enforcer (internal + same-feature InternalsVisibleTo); these tests stop
    /// anyone from quietly widening the surface again.
    /// </summary>
    public sealed class AccessRulesTests
    {
        private string _root;
        private List<Type> _types;

        [OneTimeSetUp]
        public void SetUp()
        {
            _root = SourceText.FindRepoRoot(TestContext.CurrentContext.TestDirectory);
            _types = GameTypeScanner.GameTypes().ToList();
        }

        [Test]
        public void ARCH020_LogicTypesAreInternal()
        {
            var logicNamespaces = new HashSet<string>(
                Directory.GetFiles(Path.Combine(_root, "UnityProject", "Assets", "_Game", "Features"), "*.cs", SearchOption.AllDirectories)
                    .Where(f => PathInfo.Of(Rel(f)).Layer == PathLayer.Logic)
                    .SelectMany(f => SourceRules.DeclaredNamespaces(File.ReadAllText(f))));

            Assume.That(logicNamespaces, Is.Not.Empty);

            var offenders = _types
                .Where(t => t.IsVisible && logicNamespaces.Contains(t.Namespace))
                .Select(t => "ARCH020 " + t.FullName + " is public; make it internal (the feature's Unity layer and tests see it via InternalsVisibleTo)")
                .ToList();

            Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
        }

        [Test]
        public void ARCH022_NoPublicMutableFields()
        {
            var offenders = new List<string>();
            foreach (var t in _types.Where(t => t.IsVisible && !t.IsEnum))
            {
                bool dto = t.IsDefined(typeof(SerializableAttribute), false) && t.Name.EndsWith("Data", StringComparison.Ordinal);
                if (dto)
                    continue;
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if (!f.IsInitOnly)
                        offenders.Add("ARCH022 " + t.FullName + "." + f.Name + ": public mutable field; use a property with a private setter or a method that keeps the invariant");
            }

            Assert.That(offenders, Is.Empty, string.Join("\n", offenders));
        }

        /// <summary>
        /// Public API of Core and of every Contracts folder must match its PublicApi.txt.
        /// Changing a baseline is a deliberate act: the file is CODEOWNERS-protected and denied to the agent.
        /// </summary>
        [Test]
        public void ARCH023_PublicApiMatchesBaseline()
        {
            var groups = new Dictionary<string, string> // namespace prefix → baseline path
            {
                { "Game.Core", "UnityProject/Assets/_Game/Core/Runtime/PublicApi.txt" },
            };
            foreach (var dir in Directory.GetDirectories(Path.Combine(_root, "UnityProject", "Assets", "_Game", "Features")))
                if (Directory.Exists(Path.Combine(dir, "Contracts")))
                {
                    string feature = Path.GetFileName(dir);
                    groups["Game." + feature + ".Contracts"] = "UnityProject/Assets/_Game/Features/" + feature + "/Contracts/PublicApi.txt";
                }

            var problems = new List<string>();
            foreach (var g in groups)
            {
                string actual = Describe(_types.Where(t => t.IsVisible && t.Namespace == g.Key));
                string baselinePath = Path.Combine(_root, g.Value);
                string expected = File.Exists(baselinePath) ? File.ReadAllText(baselinePath).Replace("\r\n", "\n") : "";
                if (actual == expected)
                    continue;

                string received = Path.Combine(_root, "Dashboard", "Data", "api", g.Key + ".received.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(received));
                File.WriteAllText(received, actual);

                var exp = new HashSet<string>(expected.Split('\n'));
                var act = new HashSet<string>(actual.Split('\n'));
                problems.Add("ARCH023 " + g.Value + " differs from the compiled API of " + g.Key + " (intended? run ./Tools/test-gate.sh api accept " + g.Key + " alone: the owner answers yes/no)");
                problems.AddRange(exp.Except(act).Where(l => l.Length > 0).Take(10).Select(l => "  - " + l));
                problems.AddRange(act.Except(exp).Where(l => l.Length > 0).Take(10).Select(l => "  + " + l));
            }

            Assert.That(problems, Is.Empty,
                string.Join("\n", problems) + "\nIf the change is intended, the owner accepts it (yes/no) via api accept; otherwise restore the API (prefer adding over removing).");
        }

        // ---------- API description (stable, sorted, one line per member) ----------

        internal static string Describe(IEnumerable<Type> types)
        {
            var sb = new StringBuilder();
            foreach (var t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                sb.Append(Kind(t)).Append(' ').Append(Name(t)).Append('\n');
                var lines = new List<string>();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

                if (t.IsEnum)
                    lines.AddRange(Enum.GetNames(t));
                else
                {
                    lines.AddRange(t.GetConstructors(flags).Select(c => ".ctor(" + Params(c) + ")"));
                    lines.AddRange(t.GetMethods(flags).Where(m => !m.IsSpecialName).Select(m =>
                        (m.IsStatic ? "static " : "") + Name(m.ReturnType) + " " + m.Name + Generic(m) + "(" + Params(m) + ")"));
                    lines.AddRange(t.GetProperties(flags).Select(p =>
                        ((p.GetGetMethod() ?? p.GetSetMethod())?.IsStatic == true ? "static " : "") + Name(p.PropertyType) + " " + p.Name + " {" +
                        (p.GetGetMethod() != null ? " get;" : "") + (p.GetSetMethod() != null ? " set;" : "") + " }"));
                    lines.AddRange(t.GetEvents(flags).Select(e => "event " + Name(e.EventHandlerType) + " " + e.Name));
                    lines.AddRange(t.GetFields(flags).Where(f => !f.IsSpecialName).Select(f =>
                        (f.IsLiteral ? "const " : f.IsStatic ? "static " : "") + (f.IsInitOnly ? "readonly " : "") + Name(f.FieldType) + " " + f.Name));
                }

                foreach (var line in lines.OrderBy(l => l, StringComparer.Ordinal))
                    sb.Append("  ").Append(line).Append('\n');
            }
            return sb.ToString();
        }

        private static string Kind(Type t)
        {
            if (t.IsInterface) return "interface";
            if (t.IsEnum) return "enum";
            if (t.IsValueType) return "struct";
            if (typeof(Delegate).IsAssignableFrom(t)) return "delegate";
            if (t.IsAbstract && t.IsSealed) return "static class";
            if (t.IsSealed) return "sealed class";
            if (t.IsAbstract) return "abstract class";
            return "class";
        }

        private static string Generic(MethodInfo m) =>
            m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";

        private static string Params(MethodBase m) =>
            string.Join(", ", m.GetParameters().Select(p => Name(p.ParameterType) + " " + p.Name));

        private static string Name(Type t)
        {
            if (t.IsGenericParameter) return t.Name;
            if (t.IsByRef) return "ref " + Name(t.GetElementType());
            if (t.IsArray) return Name(t.GetElementType()) + "[]";
            string name = t.IsNested ? Name(t.DeclaringType) + "." + t.Name : (t.Namespace == null ? "" : t.Namespace + ".") + t.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            return t.IsGenericType ? name + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">" : name;
        }

        private string Rel(string full) => PathInfo.Normalize(full.Substring(_root.Length));
    }
}
