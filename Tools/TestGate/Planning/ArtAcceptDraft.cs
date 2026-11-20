using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;

namespace TestGate.Planning
{
    /// <summary>
    /// The owner's multi-slot acceptance draft, saved by the dashboard's Art tab (Downloads/art-accept.draft.txt):
    ///   # comment lines
    ///   MathQuiz.Bat v004 7c41e0a2b9d3      (slot, version, content hash the owner was looking at)
    /// `art accept --draft FILE` recomputes every slot from the real art and accepts only lines that still match: the owner
    /// answers yes/no once for the whole list. The draft is a request, never a record: the gate decides what it means.
    /// </summary>
    public sealed class ArtAcceptDraft
    {
        public sealed class Line
        {
            public int Number { get; set; }
            public string Slot { get; set; }
            public string Version { get; set; }
            public string Hash { get; set; }
        }

        public sealed class Rejection
        {
            public string Slot { get; set; }
            public string Reason { get; set; }
            public override string ToString() => Slot + ": " + Reason;
        }

        public sealed class Result
        {
            public List<SlotState> Accept { get; } = new List<SlotState>();
            public List<Rejection> Rejected { get; } = new List<Rejection>();
            /// <summary>Lines needing nothing (this exact final is accepted already).</summary>
            public List<Rejection> Skipped { get; } = new List<Rejection>();
            public List<string> Notes { get; } = new List<string>();

            /// <summary>What a yes is bound to: the exact slots and contents to accept (art changing after the ask breaks the match).</summary>
            public string Bind => PlanText.Hash(string.Join("\n", Accept.Select(s => s.Id + " " + s.Hash)));
        }

        public List<Line> Lines { get; } = new List<Line>();
        public List<string> Errors { get; } = new List<string>();

        public static ArtAcceptDraft Parse(string text)
        {
            var d = new ArtAcceptDraft();
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string t = lines[i].Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal))
                    continue;
                var p = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 3 || p[0].IndexOf('.') <= 0 || !(p[1].StartsWith("v", StringComparison.Ordinal) || p[1] == "-"))
                {
                    d.Errors.Add("line " + (i + 1) + ": expected 'SLOT vNNN HASH', got: " + t);
                    continue;
                }
                d.Lines.Add(new Line { Number = i + 1, Slot = p[0], Version = p[1], Hash = p[2] });
            }
            return d;
        }

        public static ArtAcceptDraft Load(string path) =>
            File.Exists(path) ? Parse(File.ReadAllText(path)) : new ArtAcceptDraft { Errors = { "draft file not found: " + path } };

        /// <summary>Every line checked against the slots as they are NOW (level, gate, version in use, content hash).</summary>
        public Result Evaluate(IReadOnlyDictionary<string, SlotState> slots)
        {
            var r = new Result();
            var last = new Dictionary<string, Line>(StringComparer.Ordinal);
            foreach (var l in Lines)
            {
                if (last.ContainsKey(l.Slot))
                    r.Notes.Add(l.Slot + " appears more than once: line " + l.Number + " (the last one) counts");
                last[l.Slot] = l;
            }
            foreach (var l in last.Values.OrderBy(x => x.Number))
            {
                if (!slots.TryGetValue(l.Slot, out var s))
                {
                    r.Rejected.Add(new Rejection { Slot = l.Slot, Reason = "no such slot" });
                    continue;
                }
                string refused = ArtCli.Refusal(s);
                if (refused != null)
                    r.Rejected.Add(new Rejection { Slot = l.Slot, Reason = refused });
                else if (s.Hash != l.Hash || (s.Version ?? "-") != l.Version)
                    r.Rejected.Add(new Rejection { Slot = l.Slot, Reason = "the art changed after you looked at it (draft " + l.Version + " " + l.Hash +
                                                                           ", now " + (s.Version ?? "-") + " " + s.Hash + "): look again and save a new draft" });
                else if (s.Level == ArtLevels.Final)
                    r.Skipped.Add(new Rejection { Slot = l.Slot, Reason = "this final is accepted already" });
                else
                    r.Accept.Add(s);
            }
            return r;
        }
    }
}
