using System;
using System.IO;
using System.Linq;
using TestGate.AgentControl;

namespace TestGate.Planning
{
    /// <summary>
    /// `art accept --draft FILE`: the owner ticked several finals on the dashboard (Art tab) and saved a draft; the gate recomputes
    /// every slot, keeps only lines that still match the real art, shows the owner both lists and asks yes/no ONCE.
    /// One yes = exactly that list (the grant is bound to it): art that changes between the ask and the run is not accepted.
    /// </summary>
    public static class ArtAcceptDraftCli
    {
        public static (ArtAcceptDraft draft, ArtAcceptDraft.Result result, string error) Evaluate(string root, string file)
        {
            if (string.IsNullOrWhiteSpace(file))
                return (null, null, "usage: art accept --draft FILE (the file the dashboard's Art tab saved)");
            var draft = ArtAcceptDraft.Load(Path.GetFullPath(file));
            if (draft.Errors.Count > 0)
                return (draft, null, "draft " + file + ": " + string.Join("; ", draft.Errors.Take(5)));
            if (draft.Lines.Count == 0)
                return (draft, null, "draft " + file + " lists no slot: tick some finals on the dashboard's Art tab and save again");
            var result = draft.Evaluate(ArtStatus.Compute(root));
            if (result.Accept.Count == 0)
                return (draft, result, "nothing in " + file + " can be accepted: " +
                                       string.Join("; ", result.Rejected.Concat(result.Skipped).Select(r => r.ToString()).Take(6)));
            return (draft, result, null);
        }

        /// <summary>Plain text for the terminal (and the owner's dialog falls back to it).</summary>
        public static (string text, string error) Describe(string root, string file)
        {
            var (_, r, error) = Evaluate(root, file);
            return error != null ? (null, error) : (Text(r), null);
        }

        public static string Text(ArtAcceptDraft.Result r) =>
            "Accept " + r.Accept.Count + " final(s):\n" + string.Join("\n", r.Accept.Select(s => "  + " + s.Id + " " + (s.Version ?? "-") + " " + s.Hash + " " + s.BoundFile)) +
            (r.Rejected.Count > 0 ? "\nLeft out:\n" + string.Join("\n", r.Rejected.Select(x => "  - " + x)) : "") +
            (r.Skipped.Count > 0 ? "\nAlready accepted:\n" + string.Join("\n", r.Skipped.Select(x => "  = " + x)) : "") +
            (r.Notes.Count > 0 ? "\nNotes:\n" + string.Join("\n", r.Notes.Select(x => "  · " + x)) : "");

        public static int Accept(string root, string[] args)
        {
            string file = args.Length == 4 ? args[3] : null;
            var (_, r, error) = Evaluate(root, file);
            if (error != null)
            {
                Console.Error.WriteLine(error);
                return file == null ? 64 : 1;
            }
            var auth = OwnerAsk.Authorize(root, args, "art-cli", "accepting final art is the owner's decision.");
            if (auth == null)
                return 3;
            if (!auth.Human && auth.Bound != null && auth.Bound != r.Bind)
            {
                Console.Error.WriteLine("Refused: the art changed after the owner said yes (the list is not the one they saw). Run the command again.");
                return 1;
            }
            Console.WriteLine(Text(r));
            if (auth.Human)
            {
                Console.Write("Looked at them on the compare pages? Type " + r.Accept.Count + " to accept exactly these finals: ");
                if ((Console.ReadLine() ?? "").Trim() != r.Accept.Count.ToString())
                {
                    Console.WriteLine("Not accepted.");
                    return 1;
                }
            }
            Recorder.Session = "human";
            foreach (var s in r.Accept)
            {
                ArtStatus.Accept(root, s);
                Recorder.Emit(Recorder.New(EventTypes.ArtAccepted, auth.Source).With("slot", s.Id).With("file", s.BoundFile).With("hash", s.Hash)
                    .With("draft", Path.GetFileName(file)).With("toolUseId", auth.ToolUseId));
            }
            Console.WriteLine("Accepted " + r.Accept.Count + " final(s): " + string.Join(", ", r.Accept.Select(s => s.Id)) + ". Changing one later makes it STALE.");
            return 0;
        }
    }
}
