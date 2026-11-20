using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Game.Architecture.Rules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TestGate.DeadCode
{
    /// <summary>
    /// ARCH033: a type, method, property, field or event that nothing references. Roslyn syntax only (no Unity references needed):
    /// a declaration counts as used when its name appears as an identifier anywhere else in Assets/ (tests included), in a
    /// PublicApi.txt baseline, or, for fields and public methods, in a serialized asset (inspector fields, UnityEvent targets).
    /// Matching by name can miss dead code with a common name; it should not flag live code. Only declarations in the
    /// files a change touches are judged, so old dead code never turns an unrelated gate red (`deadcode --all` lists it).
    /// </summary>
    public static class DeadCodeScan
    {
        public const string RuleId = "ARCH033";

        private static readonly HashSet<string> UnityMessages = new HashSet<string>(StringComparer.Ordinal)
        {
            "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy", "OnValidate", "Reset",
            "OnApplicationPause", "OnApplicationFocus", "OnApplicationQuit", "OnGUI", "OnDrawGizmos", "OnDrawGizmosSelected",
            "OnRectTransformDimensionsChange", "OnTransformParentChanged", "OnBeforeTransformParentChanged", "OnCanvasGroupChanged",
            "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay", "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D",
            "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay", "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
            "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit", "OnBecameVisible", "OnBecameInvisible", "OnAnimatorMove",
            "OnRenderObject", "OnWillRenderObject", "OnPreRender", "OnPostRender", "OnRenderImage", "OnInspectorGUI", "OnSceneGUI",
            "OnPreprocessBuild", "OnPostprocessBuild", "OnPostprocessAllAssets", "OnPreprocessAsset", "OnPostprocessPrefab",
            "OnSelectionChange", "OnFocus", "OnLostFocus", "OnHierarchyChange", "OnProjectChange", "CreateGUI", "OnBeforeSerialize",
            "OnAfterDeserialize", "OnStateEnter", "OnStateExit", "OnStateUpdate", "Main"
        };

        /// <summary>AssetPostprocessor callbacks: OnPreprocessTexture, OnPostprocessModel, …</summary>
        private static readonly Regex UnityMessagePrefix = new Regex(@"^On(Pre|Post)process[A-Z]");

        /// <summary>Attributes through which a test runner, the editor or Unity calls the member by itself.</summary>
        private static readonly HashSet<string> EntryAttributes = new HashSet<string>(StringComparer.Ordinal)
        {
            "Test", "TestCase", "TestCaseSource", "TestFixture", "TestFixtureSource", "UnityTest", "Theory", "ValueSource",
            "SetUp", "TearDown", "OneTimeSetUp", "OneTimeTearDown", "UnitySetUp", "UnityTearDown",
            "MenuItem", "ContextMenu", "ContextMenuItem", "RuntimeInitializeOnLoadMethod", "InitializeOnLoad", "InitializeOnLoadMethod",
            "DidReloadScripts", "PostProcessBuild", "PostProcessScene", "OnOpenAsset", "Preserve", "CustomEditor", "CustomPropertyDrawer",
            "CreateAssetMenu", "Serializable", "SettingsProvider", "UsedImplicitly", "ModuleInitializer"
        };

        private static readonly Regex Word = new Regex(@"[A-Za-z_][A-Za-z0-9_]*");
        private static readonly string[] AssetExtensions = { ".prefab", ".unity", ".asset", ".controller", ".anim", ".overrideController" };

        /// <summary>Corpus of names in use: built once per scan.</summary>
        public sealed class Index
        {
            internal readonly Dictionary<string, int> Identifiers = new Dictionary<string, int>(StringComparer.Ordinal);
            internal readonly HashSet<string> AssetWords = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Names the tools and CI use from outside (-executeMethod targets, test filters).</summary>
            internal readonly HashSet<string> ToolWords = new HashSet<string>(StringComparer.Ordinal);
            /// <summary>Every type declared in Assets/: a base type not in here comes from Unity/.NET and may call members itself.</summary>
            internal readonly HashSet<string> DeclaredTypes = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> ApiWords = new HashSet<string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, SyntaxTree> Trees = new Dictionary<string, SyntaxTree>(StringComparer.Ordinal);

            public void AddSource(string rel, string text)
            {
                var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest), rel);
                Trees[rel] = tree;
                foreach (var t in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                    DeclaredTypes.Add(t.Identifier.ValueText);
                foreach (var t in tree.GetRoot().DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
                {
                    if (t.Parent is ConstructorDeclarationSyntax || t.Parent is DestructorDeclarationSyntax)
                        continue; // `Foo()` declares a constructor; it is not a use of the type Foo
                    Identifiers.TryGetValue(t.ValueText, out int n);
                    Identifiers[t.ValueText] = n + 1;
                }
            }

            public void AddAsset(string text) => AddWords(AssetWords, text);
            public void AddPublicApi(string text) => AddWords(ApiWords, text);
            public void AddTool(string text) => AddWords(ToolWords, text);

            private static void AddWords(HashSet<string> set, string text)
            {
                foreach (Match m in Word.Matches(text))
                    set.Add(m.Value);
            }
        }

        public static Index Load(string root)
        {
            var index = new Index();
            string assets = Path.Combine(root, "UnityProject", "Assets");
            if (!Directory.Exists(assets))
                return index;
            foreach (var file in Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (file.EndsWith(".cs", StringComparison.Ordinal))
                    index.AddSource(PathInfo.Normalize(file.Substring(root.Length)), File.ReadAllText(file));
                else if (name == "PublicApi.txt")
                    index.AddPublicApi(File.ReadAllText(file));
                else if (AssetExtensions.Contains(Path.GetExtension(file)))
                    index.AddAsset(File.ReadAllText(file));
            }
            foreach (var dir in new[] { Path.Combine(root, "Tools"), Path.Combine(root, ".github") }.Where(Directory.Exists))
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    string ext = Path.GetExtension(file);
                    if ((ext == ".cs" || ext == ".sh" || ext == ".yml" || ext == ".yaml") && !file.Contains("/bin/") && !file.Contains("/obj/"))
                        index.AddTool(File.ReadAllText(file));
                }
            return index;
        }

        /// <summary>Hand-written game code whose declarations are judged (not generated, not the rule engine, not vendors).</summary>
        public static bool Judged(string rel) =>
            rel.EndsWith(".cs", StringComparison.Ordinal) && rel.Contains("/_Game/") && !rel.Contains("/Generated/")
            && !rel.Contains("/_Game/Architecture/") && !rel.Contains("/_Game/Tests/Architecture/");

        public static List<RuleViolation> Check(string root, IEnumerable<string> files) => Check(Load(root), files);

        public static List<RuleViolation> Check(Index index, IEnumerable<string> files)
        {
            var result = new List<RuleViolation>();
            foreach (var rel in files.Select(PathInfo.Normalize).Where(Judged).Distinct())
                if (index.Trees.TryGetValue(rel, out var tree))
                    result.AddRange(InTree(index, rel, tree));
            return result;
        }

        public static List<RuleViolation> CheckAll(string root)
        {
            var index = Load(root);
            return Check(index, index.Trees.Keys.OrderBy(k => k, StringComparer.Ordinal));
        }

        private static IEnumerable<RuleViolation> InTree(Index index, string rel, SyntaxTree tree)
        {
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var (name, kind, token) = Declared(node);
                if (name == null || Exempt(index, node, rel))
                    continue;
                bool external = kind == "field" || IsPublic(node);
                bool used = (index.Identifiers.TryGetValue(name, out int n) && n > 1) || index.ApiWords.Contains(name)
                            || (external && (index.AssetWords.Contains(name) || index.ToolWords.Contains(name)));
                if (!used)
                    yield return new RuleViolation(RuleId, rel, token.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        kind + " '" + name + "' is never referenced: delete it (Unity or a runner calls it by name? mark it [Preserve])");
            }
        }

        private static (string name, string kind, SyntaxToken token) Declared(SyntaxNode node)
        {
            switch (node)
            {
                case ClassDeclarationSyntax c: return (c.Identifier.ValueText, "type", c.Identifier);
                case StructDeclarationSyntax s: return (s.Identifier.ValueText, "type", s.Identifier);
                case InterfaceDeclarationSyntax i: return (i.Identifier.ValueText, "type", i.Identifier);
                case EnumDeclarationSyntax e: return (e.Identifier.ValueText, "type", e.Identifier);
                case RecordDeclarationSyntax r: return (r.Identifier.ValueText, "type", r.Identifier);
                case MethodDeclarationSyntax m: return (m.Identifier.ValueText, "method", m.Identifier);
                case PropertyDeclarationSyntax p: return (p.Identifier.ValueText, "property", p.Identifier);
                case EventDeclarationSyntax ev: return (ev.Identifier.ValueText, "event", ev.Identifier);
                case VariableDeclaratorSyntax v when v.Parent?.Parent is FieldDeclarationSyntax || v.Parent?.Parent is EventFieldDeclarationSyntax:
                    return (v.Identifier.ValueText, "field", v.Identifier);
                default: return (null, null, default);
            }
        }

        private static bool Exempt(Index index, SyntaxNode node, string rel)
        {
            var member = node is VariableDeclaratorSyntax ? node.Parent.Parent : node;
            if (member is MemberDeclarationSyntax md)
            {
                if (md.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword) || m.IsKind(SyntaxKind.ExternKeyword)))
                    return true;
                if (HasEntryAttribute(md.AttributeLists))
                    return true;
            }
            // A member inside a type the runner/Unity creates by itself ([Serializable] data, [TestFixture]) is that type's business.
            if (!(member is BaseTypeDeclarationSyntax) && member.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => HasEntryAttribute(t.AttributeLists)))
                return true;
            // A public member of a type with an external interface (IPreprocessBuildWithReport.callbackOrder) may implement it.
            if (!(member is BaseTypeDeclarationSyntax) && IsPublic(member)
                && member.Parent is TypeDeclarationSyntax owner && ExternalBases(index, owner).Any(b => b.Length > 1 && b[0] == 'I' && char.IsUpper(b[1])))
                return true;
            switch (member)
            {
                case MethodDeclarationSyntax m:
                    return m.ExplicitInterfaceSpecifier != null || UnityMessages.Contains(m.Identifier.ValueText) || UnityMessagePrefix.IsMatch(m.Identifier.ValueText);
                case PropertyDeclarationSyntax p:
                    return p.ExplicitInterfaceSpecifier != null;
                case EventDeclarationSyntax e:
                    return e.ExplicitInterfaceSpecifier != null;
                case TypeDeclarationSyntax t:
                    // static (extension) holders are used through their members; a MonoBehaviour/ScriptableObject is used through
                    // its script GUID (it must live in a file of its name), not by name in code; a test class by the runner.
                    // A type with an external base (AssetPostprocessor, IPreprocessBuildWithReport) is found by Unity itself.
                    return t.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
                           || (t.BaseList != null && Path.GetFileNameWithoutExtension(rel) == t.Identifier.ValueText)
                           || ExternalBases(index, t).Any()
                           || t.Members.OfType<MemberDeclarationSyntax>().Any(x => HasEntryAttribute(x.AttributeLists));
            }
            return false;
        }

        private static bool HasEntryAttribute(SyntaxList<AttributeListSyntax> lists) =>
            lists.SelectMany(l => l.Attributes).Any(a =>
            {
                string n = a.Name is QualifiedNameSyntax q ? q.Right.Identifier.ValueText : a.Name is IdentifierNameSyntax id ? id.Identifier.ValueText : a.Name.ToString();
                if (n.EndsWith("Attribute", StringComparison.Ordinal))
                    n = n.Substring(0, n.Length - "Attribute".Length);
                return EntryAttributes.Contains(n);
            });

        private static IEnumerable<string> ExternalBases(Index index, TypeDeclarationSyntax t) =>
            (t.BaseList?.Types ?? default).Select(b => b.Type is GenericNameSyntax g ? g.Identifier.ValueText
                    : b.Type is QualifiedNameSyntax q ? q.Right.Identifier.ValueText : b.Type.ToString())
                .Where(n => !index.DeclaredTypes.Contains(n));

        private static bool IsPublic(SyntaxNode node) => node is MemberDeclarationSyntax m && m.Modifiers.Any(x => x.IsKind(SyntaxKind.PublicKeyword));
    }
}
