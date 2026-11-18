using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Game.MathQuiz.UseCases;

namespace Game.MathQuiz.Tests
{
    /// <summary>
    /// Reads the designer data the game ships (QuizConfigAsset, Force Text YAML) without Unity, so design-lint tests check
    /// Inspector edits too. Works from dotnet (bin/) and from the Unity test runner (cwd = project root).
    /// </summary>
    internal static class ShippedContent
    {
        public static IEnumerable<(string Path, DesignerArena Arena)> ArenaAssets()
        {
            string root = FindRoot();
            if (root == null)
                yield break;
            string dir = Path.Combine(root, "Assets", "_Game", "Features", "MathQuiz", "Assets");
            if (!Directory.Exists(dir))
                yield break;
            foreach (var file in Directory.GetFiles(dir, "*.asset", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (text.Contains("slimeSpeedPerSecond:"))
                    yield return (file.Substring(root.Length + 1).Replace('\\', '/'), ParseArena(text));
            }
        }

        public static DesignerArena ParseArena(string yaml)
        {
            var d = DesignerArena.Default;
            int Int(string key, int fallback) => int.TryParse(Value(yaml, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            float Vec(string key, string axis, float fallback)
            {
                var m = Regex.Match(yaml, @"^\s*" + key + @":\s*\{[^}]*\b" + axis + @":\s*(-?[0-9.]+)", RegexOptions.Multiline);
                return m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
            }
            return new DesignerArena(Int("baseLives", d.BaseLives), Vec("spawnEverySeconds", "x", d.SpawnMinSeconds), Vec("spawnEverySeconds", "y", d.SpawnMaxSeconds),
                (int)Vec("slimeSpeedPerSecond", "x", d.SpeedMinPerSecond), (int)Vec("slimeSpeedPerSecond", "y", d.SpeedMaxPerSecond), Int("hopPercent", d.HopPercent));
        }

        private static string Value(string yaml, string key)
        {
            var m = Regex.Match(yaml, @"^\s*" + key + @":\s*(\S+)\s*$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string FindRoot()
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), NUnit.Framework.TestContext.CurrentContext.TestDirectory })
                for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
                    if (Directory.Exists(Path.Combine(d.FullName, "Assets", "_Game")))
                        return d.FullName;
            return null;
        }
    }
}
