using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Game.Architecture.Rules
{
    public enum PathLayer
    {
        Outside,      // not under Assets/_Game
        Core,         // Core/Runtime
        Contracts,    // Features/F/Contracts
        Logic,        // Features/F/Logic
        Unity,        // Features/F/Unity
        Editor,       // Features/F/Editor, Core/Editor
        DevTools,     // Features/F/DevTools: fixtures, scenarios (pure, Editor + dev builds only)
        FeatureAssets,// Features/F/Assets
        FeatureRoot,  // Features/F/* (README.md, misplaced files)
        Tests,        // any Tests folder, Tests/
        Bootstrap,    // Bootstrap (incl. Bootstrap/Editor)
        Architecture, // Architecture/ (rule engine itself)
        Scenes,       // Scenes/
        Other
    }

    /// <summary>
    /// The repository layout (a hard rule, ARCH130). Root = the folder that contains AGENTS.md; every path the tools
    /// exchange is relative to Root with '/' separators. Unity's own paths ("Assets/...") are relative to UnityProject/.
    /// </summary>
    public static class Layout
    {
        public const string RootMarker = "AGENTS.md";
        public const string Unity = "UnityProject/";
        public const string Assets = Unity + "Assets/";
        public const string Packages = Unity + "Packages/";
        public const string ProjectSettings = Unity + "ProjectSettings/";
        public const string Library = Unity + "Library/";
        public const string Tools = "Tools/";
        public const string LogicTests = Tools + "Tools/LogicTests/";
        public const string Docs = "Docs/";
        public const string Plan = Docs + "Plan/";
        public const string Dashboard = "Dashboard/";
        public const string Data = Dashboard + "Data/";
        public const string Template = "Template/";

        /// <summary>Absolute path of a Root-relative path.</summary>
        public static string Full(string root, string relative) =>
            System.IO.Path.Combine(root, relative.TrimEnd('/').Replace('/', System.IO.Path.DirectorySeparatorChar));

        /// <summary>Unity asset path ("Assets/X", "Packages/Y") → Root-relative ("UnityProject/Assets/X").</summary>
        public static string FromUnity(string unityPath) => Unity + unityPath.Replace('\\', '/');

        /// <summary>Root-relative → Unity asset path; null when the path is outside UnityProject/.</summary>
        public static string ToUnity(string relative) =>
            relative.StartsWith(Unity, StringComparison.Ordinal) ? relative.Substring(Unity.Length) : null;

        /// <summary>Root from inside Unity: Application.dataPath is Root/UnityProject/Assets.</summary>
        public static string RootFromDataPath(string dataPath) =>
            System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(dataPath)));

        /// <summary>The closest folder above <paramref name="start"/> with AGENTS.md and UnityProject/.</summary>
        public static string FindRoot(string start)
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                if (File.Exists(System.IO.Path.Combine(dir.FullName, RootMarker)) && Directory.Exists(System.IO.Path.Combine(dir.FullName, "UnityProject")))
                    return dir.FullName;
            throw new DirectoryNotFoundException("No Root (a folder with " + RootMarker + " and UnityProject/) above " + start);
        }
    }

    /// <summary>Path conventions → layer/feature. Paths are Root-relative with '/' separators.</summary>
    public sealed class PathInfo
    {
        public const string GameRoot = Layout.Assets + "_Game/";

        public string Path { get; }
        public PathLayer Layer { get; }
        public string Feature { get; }

        public bool IsPure => Layer == PathLayer.Core || Layer == PathLayer.Contracts || Layer == PathLayer.Logic || Layer == PathLayer.DevTools;

        /// <summary>Core/DevTools or Features/F/DevTools: compiled only in the Editor and in builds with GAME_DEVTOOLS.</summary>
        public bool IsDevTools => Layer == PathLayer.DevTools || Path.StartsWith(GameRoot + "Core/DevTools/", StringComparison.Ordinal);
        public bool IsTest => Layer == PathLayer.Tests;
        public bool IsGameCode => Layer != PathLayer.Outside && Layer != PathLayer.Architecture;

        /// <summary>Editor-only code (any /Editor/ folder): never ships, may use reflection/editor APIs.</summary>
        public bool IsEditorOnly => Path.Contains("/Editor/");

        private PathInfo(string path, PathLayer layer, string feature)
        {
            Path = path;
            Layer = layer;
            Feature = feature;
        }

        public static string Normalize(string path) => path.Replace('\\', '/').TrimStart('.', '/');

        public static PathInfo Of(string relativePath)
        {
            string path = Normalize(relativePath);
            if (!path.StartsWith(GameRoot, StringComparison.Ordinal))
                return new PathInfo(path, PathLayer.Outside, null);

            string[] parts = path.Substring(GameRoot.Length).Split('/');
            bool inTests = Array.IndexOf(parts, "Tests") >= 0;

            switch (parts[0])
            {
                case "Core":
                    if (inTests)
                        return new PathInfo(path, PathLayer.Tests, null);
                    // Core/Editor: shared Unity editor tooling (view stage, layout probe, preview window).
                    // Core/Unity: shared runtime Unity code (art slots, graybox), Core/Editor: shared editor tooling.
                    if (parts.Length > 1 && parts[1] == "Unity")
                        return new PathInfo(path, PathLayer.Unity, null);
                    return new PathInfo(path, parts.Length > 1 && parts[1] == "Editor" ? PathLayer.Editor : PathLayer.Core, null);
                case "Bootstrap":
                    return new PathInfo(path, inTests ? PathLayer.Tests : PathLayer.Bootstrap, null);
                case "Tests":
                    return new PathInfo(path, PathLayer.Tests, null);
                case "Architecture":
                    return new PathInfo(path, PathLayer.Architecture, null);
                case "Scenes":
                    return new PathInfo(path, PathLayer.Scenes, null);
                case "Features":
                    if (parts.Length < 3)
                        return new PathInfo(path, PathLayer.Other, parts.Length > 1 ? parts[1] : null);
                    string feature = parts[1];
                    if (inTests)
                        return new PathInfo(path, PathLayer.Tests, feature);
                    switch (parts[2])
                    {
                        case "Contracts": return new PathInfo(path, PathLayer.Contracts, feature);
                        case "Logic": return new PathInfo(path, PathLayer.Logic, feature);
                        case "Unity": return new PathInfo(path, PathLayer.Unity, feature);
                        case "Editor": return new PathInfo(path, PathLayer.Editor, feature);
                        case "DevTools": return new PathInfo(path, PathLayer.DevTools, feature);
                        case "Assets": return new PathInfo(path, PathLayer.FeatureAssets, feature);
                        default: return new PathInfo(path, PathLayer.FeatureRoot, feature);
                    }
                default:
                    return new PathInfo(path, PathLayer.Other, null);
            }
        }

        /// <summary>True for Features/F/Assets/Public/**.</summary>
        public bool IsPublicAsset =>
            Feature != null && Path.StartsWith(GameRoot + "Features/" + Feature + "/Assets/Public/", StringComparison.Ordinal);
    }

    public static class SourceText
    {
        /// <summary>Removes // and /* */ comments but keeps line breaks, so line numbers stay correct.</summary>
        public static string StripComments(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", m => new string('\n', Count(m.Value, '\n')), RegexOptions.Singleline);
            return Regex.Replace(text, @"//.*$", "", RegexOptions.Multiline);
        }

        public static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++)
                if (text[i] == '\n')
                    line++;
            return line;
        }

        public static int Count(string text, char c)
        {
            int n = 0;
            foreach (char ch in text)
                if (ch == c)
                    n++;
            return n;
        }

        public static string FindRepoRoot(string start) => Layout.FindRoot(start);
    }
}
