using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TestGate.AgentControl;

namespace TestGate.Files
{
    /// <summary>
    ///   files                 summary: loose (untracked) files, orphans, junk, unused assets, disk use
    ///   files orphans         untracked files left by sessions that ended without a clean Stop (crash, kill)
    ///   files clean [--orphans|--junk|--session S] [--yes]      delete ONLY: untracked + written by an agent + unchanged since (dry run without --yes)
    ///   files clean --regenerable [--library] [--yes]           Dashboard/Data/, .scratch/, Temp/, Logs/, LogicTests bin/obj (+ Library/)
    ///   files unused          private assets nothing references (report only)
    ///   files du              what takes space
    /// </summary>
    public static class FilesCli
    {
        public static int Run(string root, string[] args)
        {
            string sub = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : "summary";
            switch (sub)
            {
                case "orphans": return Print(FilesReport.Loose(root).Where(f => f.Orphan).ToList(), "orphans");
                case "clean": return Clean(root, args);
                case "unused":
                    var unused = FilesReport.Unused(root);
                    foreach (var u in unused.Take(60))
                        Console.WriteLine(FilesReport.Human(u.Bytes).PadLeft(9) + "  " + u.Path + "  (" + u.Why + ")");
                    Console.WriteLine(unused.Count == 0 ? "No unused private assets." : unused.Count + " unused private asset(s), " + FilesReport.Human(unused.Sum(u => u.Bytes)) + ". Report only: git keeps them if you delete.");
                    return 0;
                case "du":
                    foreach (var d in FilesReport.Du(root))
                        Console.WriteLine(FilesReport.Human(d.Bytes).PadLeft(9) + "  " + d.Path.PadRight(18) + (d.Regenerable ? " [regenerable] " : " ") + d.Note);
                    return 0;
                default:
                    var loose = FilesReport.Loose(root);
                    Console.WriteLine("loose (untracked) files: " + loose.Count + " · orphans: " + loose.Count(f => f.Orphan) + " · junk/misplaced: " + loose.Count(f => f.Problems.Count > 0) +
                                      " · safe to delete: " + loose.Count(f => f.SafeToDelete && (f.Orphan || f.Problems.Count > 0)));
                    Print(loose.Where(f => f.Orphan || f.Problems.Count > 0).ToList(), null);
                    Console.WriteLine("more: files orphans · files clean --orphans|--junk [--yes] · files unused · files du");
                    return 0;
            }
        }

        private static int Print(List<LooseFile> files, string what)
        {
            foreach (var f in files)
                Console.WriteLine((f.SafeToDelete ? "[safe]  " : "[keep]  ") + f.Path + "  · by " + f.Origin + (f.Orphan ? " · ORPHAN (session ended without a clean stop)" : "") +
                                  (f.Origin != "unknown" && !f.Unchanged ? " · CHANGED since the agent wrote it" : "") +
                                  (f.Problems.Count > 0 ? " · " + string.Join("; ", f.Problems.Select(p => p.Split(':')[0])) : ""));
            if (what != null && files.Count == 0)
                Console.WriteLine("No " + what + ".");
            return 0;
        }

        private static int Clean(string root, string[] args)
        {
            bool yes = args.Contains("--yes");
            if (args.Contains("--regenerable"))
                return CleanRegenerable(root, args.Contains("--library"), yes);

            int si = Array.IndexOf(args, "--session");
            string session = si >= 0 && si + 1 < args.Length ? args[si + 1] : null;
            bool orphans = args.Contains("--orphans"), junk = args.Contains("--junk");
            if (!orphans && !junk && session == null)
            {
                Console.Error.WriteLine("Usage: files clean --orphans | --junk | --session <id> [--yes]   (or --regenerable)");
                return 64;
            }
            var loose = FilesReport.Loose(root);
            var picked = loose.Where(f => (orphans && f.Orphan) || (junk && f.Problems.Count > 0) || (session != null && f.Session == session)).ToList();
            var delete = picked.Where(f => f.SafeToDelete).ToList();
            foreach (var f in picked.Where(f => !f.SafeToDelete))
                Console.WriteLine("KEEP    " + f.Path + " (" + (f.ForHuman ? "a proposal waiting for the human" : f.Origin == "unknown" ? "not written by an observed agent: a human decides" : "changed after the agent wrote it") + ")");
            foreach (var f in delete)
            {
                Console.WriteLine((yes ? "DELETE  " : "would delete  ") + f.Path + "  (by " + f.Origin + ")");
                if (!yes)
                    continue;
                string full = Path.Combine(root, f.Path);
                if (File.Exists(full))
                    File.Delete(full);
                Recorder.Emit(Recorder.New(EventTypes.FileCleaned, "files-cli").With("path", f.Path).With("origin", f.Origin).With("reason", f.Orphan ? "orphan" : f.Problems.Count > 0 ? "junk" : "session")
                    .With("after", WorkSnapshot.Clean));
            }
            if (!yes && delete.Count > 0)
                Console.WriteLine("Dry run. Add --yes to delete " + delete.Count + " file(s).");
            if (delete.Count == 0)
                Console.WriteLine("Nothing safe to delete.");
            return 0;
        }

        private static int CleanRegenerable(string root, bool library, bool yes)
        {
            var targets = new List<string> { "Dashboard/Data", ".scratch", "UnityProject/Temp", "UnityProject/Logs", "Tools/LogicTests/bin", "Tools/LogicTests/obj" };
            if (library)
                targets.Add("UnityProject/Library");
            foreach (var t in targets)
            {
                string full = Path.Combine(root, t);
                if (!Directory.Exists(full))
                    continue;
                Console.WriteLine((yes ? "DELETE  " : "would delete  ") + t + "/  " + FilesReport.Human(FilesReport.Size(full)));
                if (yes)
                    Directory.Delete(full, true);
            }
            if (!yes)
                Console.WriteLine("Dry run. Add --yes. (Close Unity before deleting Library/ or Temp/.)");
            return 0;
        }
    }
}
