using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TestGate.AgentControl
{
    public sealed class ProjectNote
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Date { get; set; }
        public string Body { get; set; } = "";
    }

    public sealed class ProjectNotesResult
    {
        public List<ProjectNote> Notes { get; set; } = new List<ProjectNote>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Docs/Plan/Notes.md: the owner's notes, edited by an AI on request (the Dashboard only reads them).
    ///   ## N-1  Title  [date: 2026-11-19]
    ///   body
    /// </summary>
    public static class ProjectNotes
    {
        public const string RelativePath = "Docs/Plan/Notes.md";
        private static readonly Regex Heading = new Regex(@"^##\s+(N-\d+)\s+(.+?)\s+\[date:\s*(\d{4}-\d{2}-\d{2})\]\s*$");

        public static ProjectNotesResult Parse(string text)
        {
            var r = new ProjectNotesResult();
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            ProjectNote cur = null;
            var body = new List<string>();
            void Close()
            {
                if (cur != null)
                    cur.Body = string.Join("\n", body).Trim();
                body.Clear();
            }
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("##"))
                {
                    if (cur != null)
                        body.Add(lines[i]);
                    continue;
                }
                Close();
                var m = Heading.Match(lines[i]);
                cur = m.Success ? new ProjectNote { Id = m.Groups[1].Value, Title = m.Groups[2].Value, Date = m.Groups[3].Value } : null;
                if (cur == null)
                    r.Errors.Add("Notes.md:" + (i + 1) + ": expected '## N-1  Title  [date: yyyy-mm-dd]'");
                else
                    r.Notes.Add(cur);
            }
            Close();
            r.Notes = r.Notes.OrderByDescending(n => n.Date).ThenByDescending(n => int.Parse(n.Id.Substring(2))).ToList();
            return r;
        }
    }
}
