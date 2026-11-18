using System;
using System.Linq;
using TestGate.AgentControl;

namespace TestGate.Planning
{
    /// <summary>
    ///   art status            every slot: computed level, bound file, art gate result, measurement
    ///   art accept SLOT-ID    HUMAN ONLY: approve the CURRENT content of the bound art (hash); editing it later makes it STALE
    /// </summary>
    public static class ArtCli
    {
        public static int Run(string root, string[] args)
        {
            string sub = args.Length > 1 ? args[1] : "status";
            var slots = ArtStatus.Compute(root);
            if (sub == "accept")
                return Accept(root, slots, args.Length > 2 ? args[2] : null);

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

        private static int Accept(string root, System.Collections.Generic.Dictionary<string, SlotState> slots, string id)
        {
            if (id == null || !slots.TryGetValue(id, out var s))
            {
                Console.Error.WriteLine("Usage: art accept <slot id> (known: " + string.Join(", ", slots.Keys) + ")");
                return 64;
            }
            if (s.BoundFile == null)
            {
                Console.Error.WriteLine(id + " has no art bound (graybox): nothing to approve.");
                return 1;
            }
            if (!AgentShell.IsHumanTerminal)
            {
                Console.Error.WriteLine("Refused: approving art is a human decision (needs a terminal, not an agent shell).");
                Recorder.Emit(Recorder.New(EventTypes.PolicyDenied, "art-cli").With("target", "art accept " + id).With("reason", "not an interactive human terminal"));
                return 3;
            }
            Console.WriteLine(id + " → " + s.BoundFile + " (" + s.BoundAs + "), art gate: " + s.Gate + (s.Violations.Count > 0 ? " · " + string.Join("; ", s.Violations) : ""));
            Console.Write("Looked at it in the game / View Preview? Type the slot id to approve this exact content: ");
            if ((Console.ReadLine() ?? "").Trim() != id)
            {
                Console.WriteLine("Not approved.");
                return 1;
            }
            ArtStatus.Accept(root, s);
            Recorder.Session = "human";
            Recorder.Emit(Recorder.New(EventTypes.ArtAccepted, "human-terminal").With("slot", id).With("file", s.BoundFile).With("hash", s.Hash));
            Console.WriteLine("Approved " + id + " (" + s.Hash + "). Changing the art later makes this approval STALE.");
            return 0;
        }
    }
}
