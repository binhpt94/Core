using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>
    /// Per-file C# rules. Pure text analysis on comment-stripped code: fast enough to run after every edit.
    /// Regex can be fooled on purpose (aliases); the protected CI + reflection rules are the backstop.
    /// </summary>
    public static class SourceRules
    {
        public const int MaxLinesPerFile = 300;

        private sealed class Pattern
        {
            public string Id;
            public Regex Regex;
            public string Message;
            public Func<PathInfo, bool> AppliesTo;
        }

        private static bool Runtime(PathInfo p) => p.IsGameCode && !p.IsTest;
        private static bool RuntimeOutsideBootstrap(PathInfo p) => Runtime(p) && p.Layer != PathLayer.Bootstrap;

        private static readonly Pattern[] Patterns =
        {
            // ARCH040 Unity string dispatch
            new Pattern { Id = "ARCH040", AppliesTo = Runtime,
                Regex = new Regex(@"\b(SendMessage|SendMessageUpwards|BroadcastMessage|InvokeRepeating)\s*\("),
                Message = "string-based Unity dispatch; call an interface method or raise a typed event" },
            new Pattern { Id = "ARCH040", AppliesTo = Runtime,
                Regex = new Regex(@"\b(Invoke|CancelInvoke|StartCoroutine|StopCoroutine|IsInvoking)\s*\(\s*(""|nameof\s*\()"),
                Message = "method called by name; call it directly / keep the Coroutine or use UniTask with a Lifetime" },

            // ARCH041 Reflection / dynamic
            new Pattern { Id = "ARCH041", AppliesTo = p => Runtime(p) && !p.IsEditorOnly,
                Regex = new Regex(@"\bType\.GetType\s*\(|\bAssembly\.(Load|LoadFrom|LoadFile)\s*\(|\bActivator\.CreateInstance\b|\.(GetMethod|GetField|GetProperty|GetMember|GetEvent|InvokeMember)\s*\(|\bMethodInfo\b|\bdynamic\s+\w"),
                Message = "reflection/dynamic invocation hides dependencies; inject an interface instead" },

            // ARCH042 Lookup by name
            new Pattern { Id = "ARCH042", AppliesTo = Runtime,
                Regex = new Regex(@"\bGameObject\.Find(WithTag|GameObjectsWithTag)?\s*\(|\.Find\s*\(\s*""|\bResources\.Load(All|Async)?\b|\bShader\.Find\s*\(|\bAddComponent\s*\(\s*"""),
                Message = "lookup by name/path breaks on rename; use a serialized reference or the feature's asset entry" },

            // ARCH043 Scene search & singletons outside Bootstrap
            new Pattern { Id = "ARCH043", AppliesTo = RuntimeOutsideBootstrap,
                Regex = new Regex(@"\bFind(First|Any)?Objects?(OfType|ByType)\b"),
                Message = "scene search makes init order implicit; let Bootstrap pass the dependency in" },
            new Pattern { Id = "ARCH043", AppliesTo = p => RuntimeOutsideBootstrap(p) && p.Layer != PathLayer.Core,
                Regex = new Regex(@"\bstatic\s+(readonly\s+)?[\w<>\[\],. ]+\s+Instance\b"),
                Message = "singleton; register the service in the composition root and inject it" },

            // ARCH044 Animator parameters by string
            new Pattern { Id = "ARCH044", AppliesTo = Runtime,
                Regex = new Regex(@"\.(SetTrigger|ResetTrigger|SetBool|SetFloat|SetInteger|Play|PlayInFixedTime|CrossFade|CrossFadeInFixedTime)\s*\(\s*"""),
                Message = "Animator by string; use static readonly int X = Animator.StringToHash(\"..\")" },

            // ARCH050 Unity in pure code
            new Pattern { Id = "ARCH050", AppliesTo = p => p.IsPure,
                Regex = new Regex(@"\bUnityEngine\b|\bUnityEditor\b"),
                Message = "pure code must not use UnityEngine/UnityEditor" },

            // ARCH051 Non-determinism in Logic (Core owns the SystemRandom adapter)
            new Pattern { Id = "ARCH051", AppliesTo = p => p.Layer == PathLayer.Logic || p.Layer == PathLayer.Contracts,
                Regex = new Regex(@"new\s+System\.Random\s*\(|new\s+Random\s*\(|DateTime\.(Now|UtcNow)|Guid\.NewGuid"),
                Message = "inject IRandom / a clock" },

            // ARCH052 Direct waiting in pure code
            new Pattern { Id = "ARCH052", AppliesTo = p => p.IsPure,
                Regex = new Regex(@"\b(UniTask|Task)\.(Delay|DelayFrame|Yield|NextFrame|WaitForSeconds)\s*\(|Thread\.Sleep\s*\("),
                Message = "inject IDelay so tests can drive time" },

            // ARCH053 Forget outside Lifetime
            new Pattern { Id = "ARCH053", AppliesTo = p => Runtime(p) && !p.Path.EndsWith("/Core/Runtime/Lifetime.cs", StringComparison.Ordinal),
                Regex = new Regex(@"\.Forget\s*\("),
                Message = "work without an owner; use lifetime.Run(token => ...)" },

            // ARCH055 GetHashCode is process-randomised for strings and runtime-specific in general
            new Pattern { Id = "ARCH055", AppliesTo = p => p.IsPure,
                Regex = new Regex(@"\.GetHashCode\s*\(|\bHashCode\.Combine\s*\("),
                Message = "GetHashCode() changes per process/runtime; for seeds use RandomStreams.Derive, for state use StateHashBuilder" },

            // ARCH056 floating point in the deterministic core
            new Pattern { Id = "ARCH056", AppliesTo = p => p.Path.Contains("/Logic/Domain/") || p.Path.Contains("/Logic/Simulation/"),
                Regex = new Regex(@"\b(float|double|decimal|Single|Double|Decimal)\b|\bMath[F]?\.(Sqrt|Sin|Cos|Tan|Pow|Exp|Log|Round|Floor|Ceiling)\s*\(|(?<![\w.])\d[\d_]*(\.\d+)?([eE][+-]?\d+)?[fFdDmM]\b|(?<![\w.])\d[\d_]*\.\d+\b"),
                Message = "floating point in Domain/Simulation can differ across CPUs/runtimes; use integers (positions in 1/1000, durations in ticks) and convert in the presenter/view" },

            // ARCH030 partial
            new Pattern { Id = "ARCH030", AppliesTo = Runtime,
                Regex = new Regex(@"\bpartial\s+(class|struct|interface|record)\b"),
                Message = "partial splits files, not responsibilities; extract a separate type (generated code: Generated/ + // <auto-generated>)" },
        };

        private static readonly string[] ForbiddenNamespaceSegments =
            { "Application", "Unity", "UnityEngine", "UnityEditor", "Editor", "Random", "Debug", "Object", "Input", "Time", "Screen" };

        private static readonly Regex NamespaceDecl = new Regex(@"^\s*namespace\s+([\w.]+)", RegexOptions.Multiline);
        private static readonly Regex Ivt = new Regex(@"InternalsVisibleTo\s*\(\s*""([^""]+)""");
        private static readonly Regex PrimitiveField = new Regex(
            @"(?<attrs>(\[[^\]]*\]\s*)*)(?<mod>(public|private|protected|internal)\s+)?(?<type>int|float|double|bool|string|long|short|byte|uint)\s+(?<name>\w+)\s*(=(?!>)[^;]*)?;");
        private static readonly Regex SoPublicField = new Regex(
            @"(?:^|[{;}])\s*public\s+(?!const\b|static\b|readonly\b|class\b|sealed\b|abstract\b|override\b|virtual\b|event\b|enum\b|struct\b|interface\b)[\w<>\[\],. ]+\s+\w+\s*(=[^>]|;)", RegexOptions.Multiline);
        private static readonly Regex SoPublicSetter = new Regex(@"public[^;{(]*\{[^}]*\bset\b", RegexOptions.Singleline);

        public static IEnumerable<RuleViolation> Check(string relativePath, string content)
        {
            var info = PathInfo.Of(relativePath);
            if (!info.IsGameCode || !info.Path.EndsWith(".cs", StringComparison.Ordinal))
                yield break;

            string code = SourceText.StripComments(content);
            bool generated = info.Path.Contains("/Generated/") && content.TrimStart().StartsWith("// <auto-generated>", StringComparison.Ordinal);

            foreach (var p in Patterns)
            {
                if (!p.AppliesTo(info) || (generated && p.Id == "ARCH030"))
                    continue;
                foreach (Match m in p.Regex.Matches(code))
                    yield return new RuleViolation(p.Id, info.Path, SourceText.LineOf(code, m.Index), p.Message);
            }

            // ARCH031 file size (runtime code only; tests may be long)
            if (!info.IsTest && !generated)
            {
                int lines = SourceText.Count(content, '\n') + 1;
                if (lines > MaxLinesPerFile)
                    yield return new RuleViolation("ARCH031", info.Path, 0,
                        lines + " lines > " + MaxLinesPerFile + "; split by responsibility (not with partial)");
            }

            // ARCH054 namespace shadowing (all game code incl. tests)
            foreach (Match m in NamespaceDecl.Matches(code))
            {
                var bad = m.Groups[1].Value.Split('.').Skip(1).FirstOrDefault(s => Array.IndexOf(ForbiddenNamespaceSegments, s) >= 0);
                if (bad != null)
                    yield return new RuleViolation("ARCH054", info.Path, SourceText.LineOf(code, m.Index),
                        "namespace segment '." + bad + "' shadows a Unity type; use UseCases/Presentation/Infrastructure/EditorTools");
            }

            // ARCH021 InternalsVisibleTo only within the feature
            foreach (Match m in Ivt.Matches(code))
            {
                string target = m.Groups[1].Value;
                string prefix = info.Feature != null ? "Game." + info.Feature + "." : "Game.Core.";
                bool ok = target.StartsWith(prefix, StringComparison.Ordinal)
                          && (target.EndsWith(".Unity", StringComparison.Ordinal) || target.EndsWith(".Tests", StringComparison.Ordinal) || target.EndsWith(".Editor", StringComparison.Ordinal)
                              || target.EndsWith(".DevTools", StringComparison.Ordinal));
                if (!ok)
                    yield return new RuleViolation("ARCH021", info.Path, SourceText.LineOf(code, m.Index),
                        "InternalsVisibleTo(\"" + target + "\") leaves the feature; expose a Contract instead");
            }

            // ARCH057 views see view interfaces and view models only, never the simulation or domain
            if ((info.Layer == PathLayer.Unity || info.Layer == PathLayer.Editor) && Regex.IsMatch(code, @":\s*MonoBehaviour\b"))
                foreach (Match m in Regex.Matches(code, @"\busing\s+(Game\.\w+\.(?:Domain|Simulation))\s*;|\b(Game\.\w+\.(?:Domain|Simulation))\.\w+"))
                    yield return new RuleViolation("ARCH057", info.Path, SourceText.LineOf(code, m.Index),
                        "MonoBehaviour references " + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) +
                        "; views get view models through I<Feature>View and send input as events, so they can't read or mutate gameplay state");

            // ARCH070 serialized config values on feature MonoBehaviours
            if ((info.Layer == PathLayer.Unity || info.Layer == PathLayer.Editor) && Regex.IsMatch(code, @":\s*MonoBehaviour\b"))
            {
                foreach (Match m in PrimitiveField.Matches(code))
                {
                    string attrs = m.Groups["attrs"].Value;
                    bool serialized = attrs.Contains("SerializeField") || m.Groups["mod"].Value.Trim() == "public";
                    if (serialized && !attrs.Contains("VisualTuning") && !attrs.Contains("NonSerialized"))
                        yield return new RuleViolation("ARCH070", info.Path, SourceText.LineOf(code, m.Index),
                            "serialized " + m.Groups["type"].Value + " '" + m.Groups["name"].Value +
                            "' on a MonoBehaviour: changing its C# initializer won't update existing prefabs/scenes. Put config in the feature's ScriptableObject (or mark [VisualTuning] if purely visual)");
                }
            }

            foreach (var v in CheckLayoutWrites(info, content, code))
                yield return v;

            // ARCH071 ScriptableObjects are read-only data
            if (!info.IsTest && Regex.IsMatch(code, @":\s*ScriptableObject\b"))
            {
                var m = SoPublicField.Match(code);
                if (!m.Success)
                    m = SoPublicSetter.Match(code);
                if (m.Success)
                    yield return new RuleViolation("ARCH071", info.Path, SourceText.LineOf(code, m.Index),
                        "ScriptableObject with public mutable state; use private [SerializeField] + a getter/ToConfig()");
            }
        }

        // ARCH100: static layout (positions, sizes, anchors, spacing) is data of the prefab / layout recipe.
        // Views may only move things from view-model data, on lines marked `// layout: dynamic`, without constants.
        // Result: a "2px" change can only live in Views/<V>/<V>Layout.cs or in the prefab, so the gate knows it is
        // a layout change of exactly that view.
        private static readonly Regex LayoutWrite = new Regex(
            @"\.(anchoredPosition|anchoredPosition3D|sizeDelta|offsetMin|offsetMax|anchorMin|anchorMax|pivot|padding|spacing|preferredHeight|preferredWidth|minHeight|minWidth|flexibleHeight|flexibleWidth)\s*=(?!=)|\.(SetSizeWithCurrentAnchors|SetInsetAndSizeFromParentEdge)\s*\(");
        private static readonly Regex LayoutConstant = new Regex(@"(?<![\w.])(?!(0|1)(\.0+)?[fFdD]?(?![\w.]))\d[\d_]*(\.\d+)?[fFdD]?\b");
        public const string DynamicLayoutMarker = "// layout: dynamic";

        public static bool IsLayoutRecipe(string path) =>
            path.EndsWith("Layout.cs", StringComparison.Ordinal) && path.Contains("/Unity/Views/");

        private static IEnumerable<RuleViolation> CheckLayoutWrites(PathInfo info, string content, string code)
        {
            if (info.Layer != PathLayer.Unity || IsLayoutRecipe(info.Path))
                yield break;
            var raw = content.Replace("\r\n", "\n").Split('\n');
            var lines = code.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length && i < raw.Length; i++)
            {
                if (!LayoutWrite.IsMatch(lines[i]))
                    continue;
                if (!raw[i].Contains(DynamicLayoutMarker))
                    yield return new RuleViolation("ARCH100", info.Path, i + 1,
                        "layout written in code outside the layout recipe; put static layout in Views/<View>/<View>Layout.cs (or the prefab). Data-driven movement: end the line with `" + DynamicLayoutMarker + "`");
                else if (LayoutConstant.IsMatch(lines[i]))
                    yield return new RuleViolation("ARCH100", info.Path, i + 1,
                        "dynamic layout line contains a constant; constants (sizes, bands, offsets) belong in the layout recipe/prefab, the view only applies view-model values");
            }
        }

        /// <summary>Namespaces declared in a file (used by reflection rules to find Logic types).</summary>
        public static IEnumerable<string> DeclaredNamespaces(string content) =>
            NamespaceDecl.Matches(SourceText.StripComments(content)).Cast<Match>().Select(m => m.Groups[1].Value);
    }
}
