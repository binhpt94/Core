using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;

namespace TestGate.Planning
{
    /// <summary>
    /// The style lock of a slot: free text the owner keeps in the feature (copy/delete with the folder), never in C#.
    ///   Features/&lt;F&gt;/Assets/Private/Art/_feature.style.md   shared by every slot of the feature (palette, mood, references)
    ///   Features/&lt;F&gt;/Assets/Private/Art/&lt;Name&gt;/&lt;Name&gt;.style.md  this slot only (next to its versions); `art note`
    ///   appends to its "Rejected" section. The older flat path Art/&lt;Name&gt;.style.md is still read (and appended to) when it exists.
    /// Both are pasted into the generated brief and AI prompt, so feedback given once is never given again.
    /// </summary>
    public static class ArtStyle
    {
        public const string RejectedHeading = "## Rejected (do not repeat)";

        public static string FeaturePath(string feature) => Layout.Unity + "Assets/_Game/Features/" + feature + "/Assets/Private/Art/_feature.style.md";
        public static string SlotPath(string feature, string name) => Layout.Unity + "Assets/_Game/Features/" + feature + "/Assets/Private/Art/" + name + "/" + name + ".style.md";
        public static string LegacySlotPath(string feature, string name) => Layout.Unity + "Assets/_Game/Features/" + feature + "/Assets/Private/Art/" + name + ".style.md";

        /// <summary>The slot style file in use: the legacy flat one if it exists, else the one next to the versions.</summary>
        public static string SlotFile(string root, string feature, string name) =>
            System.IO.File.Exists(Layout.Full(root, LegacySlotPath(feature, name))) ? LegacySlotPath(feature, name) : SlotPath(feature, name);

        /// <summary>(feature text, slot text); "" when a file does not exist.</summary>
        public static (string feature, string slot) Load(string root, ArtSlotSpec s) =>
            (Clean(Read(root, FeaturePath(s.Feature))), Clean(Read(root, SlotFile(root, s.Feature, s.Name))));

        /// <summary>Drops the file's title ("# ...") and one-line HTML comments (notes for the owner, not for the prompt).</summary>
        public static string Clean(string text) =>
            string.Join("\n", (text ?? "").Replace("\r", "").Split('\n')
                .Where(l => !l.StartsWith("# ", StringComparison.Ordinal) && !(l.TrimStart().StartsWith("<!--", StringComparison.Ordinal) && l.TrimEnd().EndsWith("-->", StringComparison.Ordinal))))
                .Trim();

        /// <summary>The "Rejected" bullet lines of the slot file (what the prompt must avoid).</summary>
        public static List<string> Rejections(string slotText)
        {
            var lines = (slotText ?? "").Replace("\r", "").Split('\n');
            int at = Array.FindIndex(lines, l => l.Trim() == RejectedHeading);
            if (at < 0)
                return new List<string>();
            return lines.Skip(at + 1).TakeWhile(l => !l.StartsWith("## ", StringComparison.Ordinal))
                .Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l.Substring(2).Trim()).ToList();
        }

        /// <summary>The slot text without its Rejected section (rendered separately).</summary>
        public static string WithoutRejections(string slotText)
        {
            var lines = (slotText ?? "").Replace("\r", "").Split('\n').ToList();
            int at = lines.FindIndex(l => l.Trim() == RejectedHeading);
            if (at < 0)
                return (slotText ?? "").Trim();
            int end = lines.FindIndex(at + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
            lines.RemoveRange(at, (end < 0 ? lines.Count : end) - at);
            return string.Join("\n", lines).Trim();
        }

        /// <summary>Appends "- date: reason" under the Rejected heading of the slot file (created if missing). Returns the path.</summary>
        public static string AppendRejection(string root, ArtSlotSpec s, string reason, string date)
        {
            string rel = SlotFile(root, s.Feature, s.Name), full = Layout.Full(root, rel);
            string text = File.Exists(full) ? File.ReadAllText(full).Replace("\r", "") : "# " + s.Id + " style\n\n";
            string bullet = "- " + date + ": " + reason.Replace('\n', ' ').Trim();
            var lines = text.TrimEnd('\n').Split('\n').ToList();
            int at = lines.FindIndex(l => l.Trim() == RejectedHeading);
            if (at < 0)
            {
                lines.Add("");
                lines.Add(RejectedHeading);
                lines.Add(bullet);
            }
            else
            {
                int end = lines.FindIndex(at + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
                int insert = end < 0 ? lines.Count : end;
                while (insert > at + 1 && lines[insert - 1].Trim().Length == 0)
                    insert--;
                lines.Insert(insert, bullet);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, string.Join("\n", lines) + "\n");
            return rel;
        }

        private static string Read(string root, string rel)
        {
            string full = Layout.Full(root, rel);
            return File.Exists(full) ? File.ReadAllText(full).Replace("\r", "").Trim() : "";
        }
    }
}
