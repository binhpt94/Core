using System;
using System.Linq;
using TestGate.AgentControl;

namespace TestGate.Planning
{
    /// <summary>
    ///   art status            every slot: computed level, bound file, art gate result, measurement
    ///   art brief [SLOT|--all] generated brief + AI prompt + compare page per slot (ArtBriefCli; `art compare` = same)
    ///   art note SLOT "why"   record a rejection reason in the slot's style lock (ArtBriefCli)
    ///   art add | versions | use | codex   art versions per slot (ArtVersionCli)
    ///   art accept SLOT-ID    OWNER (terminal, or yes/no in Claude Code): accept the CURRENT content of the bound FINAL (hash) once its art gate
    ///                         passed; editing it later makes it STALE. Candidates need no acceptance: the automatic art gate is enough.
    ///   art accept --draft FILE   OWNER: accept every slot of a dashboard draft that still matches the real art (ArtAcceptDraftCli)
    /// </summary>
    public static class ArtCli
    {
        public static int Run(string root, string[] args)
        {
            string sub = args.Length > 1 ? args[1] : "status";
            if (sub == "brief" || sub == "compare")
                return ArtBriefCli.Brief(root, args);
            if (sub == "note")
                return ArtBriefCli.Note(root, args);
            if (sub == "shots")
                return ArtBriefCli.ShotsCommand(root, args);
            if (sub == "add")
                return ArtVersionCli.Add(root, args);
            if (sub == "versions")
                return ArtVersionCli.Versions(root, args);
            if (sub == "use")
                return ArtVersionCli.Use(root, args);
            if (sub == "codex")
                return ArtVersionCli.Codex(root, args);
            if (sub == "accept" && args.Length > 2 && args[2] == "--draft")
                return ArtAcceptDraftCli.Accept(root, args);
            var slots = ArtStatus.Compute(root);
            if (sub == "accept")
                return Accept(root, slots, args.Length > 2 ? args[2] : null, args);

            if (slots.Count == 0)
            {
                Console.WriteLine("No art slots declared (Features/F/Logic/Domain/<F>ArtSlots.cs).");
                return 0;
            }
            foreach (var s in slots.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                Console.WriteLine(s.Id.PadRight(22) + s.Level.PadRight(11) + "gate " + s.Gate.PadRight(9) + (s.BoundFile ?? "(graybox)"));
                Console.WriteLine(new string(' ', 22) + s.Detail + (s.Measured != null ? " · " + s.Measured : ""));
            }
            return 0;
        }

        /// <summary>What `art accept` would approve (terminal and the owner's yes/no dialog).</summary>
        public static (string text, string error) DescribeAccept(string root, string id)
        {
            var slots = ArtStatus.Compute(root);
            if (id == null || !slots.TryGetValue(id, out var s))
                return (null, "unknown slot '" + id + "' (known: " + string.Join(", ", slots.Keys) + ")");
            string refused = Refusal(s);
            if (refused != null)
                return (null, refused);
            return ("Accept final art " + Line(s) + "\n  (looked at it in the game / View Preview? changing it later makes this acceptance STALE)", null);
        }

        /// <summary>Why acceptance does not apply (null = it does): only a final whose automatic art gate passed is the owner's to accept.</summary>
        public static string Refusal(SlotState s)
        {
            if (s.BoundFile == null)
                return s.Id + " has no art bound (graybox): nothing to accept";
            if (s.BoundAs != "final")
                return s.Id + " is a candidate: candidates need only the automatic art gate, no acceptance. Acceptance is for the final " +
                       "(`art codex " + s.Id + "` or `art use " + s.Id + " vNNN --as final`)";
            if (s.Gate != "passed")
                return s.Id + " final: the art gate " + (s.Gate == "failed" ? "FAILED (" + string.Join("; ", s.Violations.Take(3)) + "): fix the art first"
                    : "has not run on this content yet (run the gate with Unity: ./Tools/test-gate.sh run)") + "; the owner accepts only what the machine passed";
            return null;
        }

        private static string Line(SlotState s) =>
            s.Id + " → " + s.BoundFile + " (" + s.BoundAs + "), hash " + s.Hash + ", art gate: " + s.Gate + (s.Violations.Count > 0 ? " · " + string.Join("; ", s.Violations) : "");

        private static int Accept(string root, System.Collections.Generic.Dictionary<string, SlotState> slots, string id, string[] args)
        {
            if (id == null || !slots.TryGetValue(id, out var s))
            {
                Console.Error.WriteLine("Usage: art accept <slot id> (known: " + string.Join(", ", slots.Keys) + ")");
                return 64;
            }
            string refused = Refusal(s);
            if (refused != null)
            {
                Console.Error.WriteLine(refused + ".");
                return 1;
            }
            var auth = OwnerAsk.Authorize(root, args, "art-cli", "accepting final art is the owner's decision.");
            if (auth == null)
                return 3;
            Console.WriteLine(Line(s));
            if (auth.Human)
            {
                Console.Write("Looked at it in the game / View Preview? Type the slot id to accept this exact final:");
                if ((Console.ReadLine() ?? "").Trim() != id)
                {
                    Console.WriteLine("Not approved.");
                    return 1;
                }
            }
            ArtStatus.Accept(root, s);
            Recorder.Session = "human";
            Recorder.Emit(Recorder.New(EventTypes.ArtAccepted, auth.Source).With("slot", id).With("file", s.BoundFile).With("hash", s.Hash).With("toolUseId", auth.ToolUseId));
            Console.WriteLine("Accepted final " + id + " (" + s.Hash + "). Changing the art later makes this acceptance STALE.");
            return 0;
        }
    }
}
