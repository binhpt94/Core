using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    /// <summary>
    /// Asset rules on Unity's text YAML (requires Force Text serialization, checked by ARCH084).
    /// Every reference in a .prefab/.unity/.asset is "guid: xxxx"; .meta files map guid → path.
    /// That's enough to check feature isolation in milliseconds, without starting Unity.
    /// </summary>
    public sealed class AssetRules
    {
        /// <summary>Text-serialized Unity files that can hold references.</summary>
        public static readonly IReadOnlyList<string> YamlExtensions = new List<string>
            { ".prefab", ".unity", ".asset", ".mat", ".controller", ".overrideController", ".anim", ".playable", ".mask", ".spriteatlas", ".lighting", ".mixer", ".physicMaterial", ".physicsMaterial2D", ".renderTexture", ".flare", ".guiskin", ".fontsettings" };

        /// <summary>Files that count as "assets" and must live in Features/F/Assets/{Public,Private}.</summary>
        public static readonly IReadOnlyList<string> AssetExtensions = YamlExtensions.Concat(new[]
            { ".png", ".jpg", ".jpeg", ".psd", ".tga", ".tif", ".exr", ".wav", ".mp3", ".ogg", ".aif", ".ttf", ".otf", ".shader", ".shadergraph", ".hlsl", ".fbx", ".obj", ".blend", ".mp4", ".webm" }).ToList();

        /// <summary>Scene-level prefab overrides that are layout, not data (ARCH081).</summary>
        public static readonly IReadOnlyList<string> AllowedSceneOverrides = new List<string>
        {
            "m_Name", "m_IsActive", "m_RootOrder", "m_StaticEditorFlags",
            "m_LocalPosition", "m_LocalRotation", "m_LocalScale", "m_LocalEulerAnglesHint", "m_ConstrainProportionsScale",
            "m_AnchoredPosition", "m_SizeDelta", "m_AnchorMin", "m_AnchorMax", "m_Pivot",
        };

        private static readonly Regex GuidRef = new Regex(@"guid: ([0-9a-f]{32})");
        private static readonly Regex MetaGuid = new Regex(@"^guid: ([0-9a-f]{32})", RegexOptions.Multiline);
        private static readonly Regex PropertyPath = new Regex(@"propertyPath: ([^\s]+)");
        private static readonly Regex MissingScript = new Regex(@"m_Script: \{fileID: 0\}");
        private static readonly Regex ScriptRef = new Regex(@"m_Script: \{fileID: -?\d+, guid: ([0-9a-f]{32})");

        private readonly string _root;
        private Dictionary<string, string> _guidToPath;
        private bool _packageCacheIndexed;

        public AssetRules(string repoRoot) => _root = repoRoot;

        /// <summary>guid → repo-relative path, built lazily from .meta files.</summary>
        public Dictionary<string, string> GuidIndex
        {
            get
            {
                if (_guidToPath != null)
                    return _guidToPath;

                _guidToPath = new Dictionary<string, string>();
                Index(Path.Combine(_root, "Assets"));
                Index(Path.Combine(_root, "Packages"));
                string cache = Path.Combine(_root, "Library", "PackageCache");
                _packageCacheIndexed = Directory.Exists(cache);
                if (_packageCacheIndexed)
                    Index(cache);
                return _guidToPath;
            }
        }

        private void Index(string dir)
        {
            if (!Directory.Exists(dir))
                return;
            foreach (var meta in Directory.EnumerateFiles(dir, "*.meta", SearchOption.AllDirectories))
            {
                string head;
                using (var reader = new StreamReader(meta))
                {
                    var buffer = new char[256];
                    int n = reader.Read(buffer, 0, buffer.Length);
                    head = new string(buffer, 0, n);
                }
                var m = MetaGuid.Match(head);
                if (m.Success)
                    _guidToPath[m.Groups[1].Value] = PathInfo.Normalize(meta.Substring(_root.Length, meta.Length - _root.Length - 5));
            }
        }

        public static bool IsYaml(string path) => YamlExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        public static bool IsAsset(string path) => AssetExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary>Rules for one asset file (repo-relative path).</summary>
        public IEnumerable<RuleViolation> CheckFile(string relativePath)
        {
            var info = PathInfo.Of(relativePath);
            string path = info.Path;

            // ARCH083 Resources folders (anything under Assets/_Game)
            if (info.Layer != PathLayer.Outside && path.Contains("/Resources/"))
                yield return new RuleViolation("ARCH083", path, 0, "Resources/ loads by string and ships everything; use a serialized reference in the feature's asset entry");

            // ARCH082 feature assets must be in Assets/Public or Assets/Private
            if (info.Feature != null && IsAsset(path) && !info.IsTest
                && !path.StartsWith(PathInfo.GameRoot + "Features/" + info.Feature + "/Assets/Public/", StringComparison.Ordinal)
                && !path.StartsWith(PathInfo.GameRoot + "Features/" + info.Feature + "/Assets/Private/", StringComparison.Ordinal))
                yield return new RuleViolation("ARCH082", path, 0, "move to Features/" + info.Feature + "/Assets/Private/ (or Public/ if other features may reference it)");

            if (!IsYaml(path) || info.Layer == PathLayer.Outside)
                yield break;

            string full = Path.Combine(_root, path);
            if (!File.Exists(full))
                yield break;
            string text = File.ReadAllText(full);

            // Fail closed: without its .meta, nobody can reference this asset and its GUID refs are UNKNOWN.
            if (!File.Exists(full + ".meta"))
                yield return new RuleViolation("ARCH084", path, 0, "missing .meta: asset references are UNKNOWN (open the project in Unity, then commit the .meta files)");

            // ARCH073 missing scripts
            foreach (Match m in MissingScript.Matches(text))
                yield return new RuleViolation("ARCH073", path, SourceText.LineOf(text, m.Index), "missing script (fileID: 0): remove the component or restore the script");
            var index = GuidIndex;
            if (_packageCacheIndexed)
                foreach (Match m in ScriptRef.Matches(text))
                    if (!index.ContainsKey(m.Groups[1].Value))
                        yield return new RuleViolation("ARCH073", path, SourceText.LineOf(text, m.Index), "script guid " + m.Groups[1].Value + " not found (deleted or renamed without its .meta)");

            // ARCH080 cross-feature references must target Public assets
            foreach (Match m in GuidRef.Matches(text))
            {
                if (!index.TryGetValue(m.Groups[1].Value, out var targetPath))
                    continue;
                var target = PathInfo.Of(targetPath);
                if (target.Feature == null || target.Feature == info.Feature || target.IsPublicAsset)
                    continue;
                yield return new RuleViolation("ARCH080", path, SourceText.LineOf(text, m.Index),
                    "references " + targetPath + " (private to feature " + target.Feature + "); reference its Assets/Public entry instead");
            }

            // ARCH081 scenes may override only layout properties of prefab instances
            if (path.EndsWith(".unity", StringComparison.Ordinal))
                foreach (Match m in PropertyPath.Matches(text))
                {
                    string root = m.Groups[1].Value.Split('.')[0];
                    if (!AllowedSceneOverrides.Contains(root))
                        yield return new RuleViolation("ARCH081", path, SourceText.LineOf(text, m.Index),
                            "scene overrides '" + m.Groups[1].Value + "'; data belongs to the prefab/config asset, the scene only places things");
                }
        }

        /// <summary>ARCH084: Force Text serialization is a precondition for every other asset rule (and for merging).</summary>
        public IEnumerable<RuleViolation> CheckProjectSettings()
        {
            string settings = Path.Combine(_root, "ProjectSettings", "EditorSettings.asset");
            const string rel = "ProjectSettings/EditorSettings.asset";
            if (!File.Exists(settings))
                yield return new RuleViolation("ARCH084", rel, 0, "missing; set Project Settings > Editor > Asset Serialization = Force Text");
            else if (!Regex.IsMatch(File.ReadAllText(settings), @"m_SerializationMode: 2\b"))
                yield return new RuleViolation("ARCH084", rel, 0, "Asset Serialization must be Force Text (m_SerializationMode: 2)");
        }

        /// <summary>Files outside <paramref name="folder"/> that reference any asset inside it (for safe deletion).</summary>
        public IEnumerable<string> IncomingReferences(string folder)
        {
            folder = PathInfo.Normalize(folder).TrimEnd('/') + "/";
            var inside = new HashSet<string>(GuidIndex.Where(kv => kv.Value.StartsWith(folder, StringComparison.Ordinal)).Select(kv => kv.Key));
            if (inside.Count == 0)
                yield break;

            foreach (var file in Directory.EnumerateFiles(Path.Combine(_root, "Assets"), "*", SearchOption.AllDirectories))
            {
                string rel = PathInfo.Normalize(file.Substring(_root.Length));
                if (rel.StartsWith(folder, StringComparison.Ordinal) || !IsYaml(rel))
                    continue;
                string text = File.ReadAllText(file);
                foreach (Match m in GuidRef.Matches(text))
                    if (inside.Contains(m.Groups[1].Value))
                    {
                        yield return rel + ":" + SourceText.LineOf(text, m.Index) + " -> " + GuidIndex[m.Groups[1].Value];
                        break;
                    }
            }
        }
    }
}
