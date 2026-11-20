using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;

namespace TestGate.Planning
{
    /// <summary>
    ///   art add SLOT FILE.svg|png --tier candidate|final --by claude|codex|human [--parent vNNN] [--note "..."] [--keep-use]
    ///   art versions SLOT                 history: tier, author, parent, note, which version each tier uses, which are approved
    ///   art use SLOT vNNN [--as TIER]     switch / roll back the version a tier uses (Unity's ArtImport rebinds it)
    ///   art codex SLOT [--from vNNN] [--dry-run]   FINAL art by Codex CLI (image_gen), from a reference version; costs money:
    ///                                     run it only when the owner asked. The result is added as a new final version.
    /// </summary>
    public static class ArtVersionCli
    {
        public static int Add(string root, string[] args)
        {
            if (args.Length < 4)
                return Usage("art add <slot> <file.svg|png> --tier candidate|final --by claude|codex|human [--parent vNNN] [--note \"...\"] [--keep-use]");
            var spec = Spec(root, args[2]);
            if (spec == null)
                return 64;
            try
            {
                var v = ArtVersions.Add(root, spec.Id, Path.GetFullPath(args[3]), Opt(args, "--tier") ?? "candidate", Opt(args, "--by") ?? "claude",
                    Opt(args, "--note"), Opt(args, "--parent"), Today(), Inkscape, args.Contains("--keep-use"));
                Console.WriteLine(spec.Id + " " + v.Id + " (" + v.Tier + ", " + v.By + ") → " + ArtVersions.Dir(spec.Id) + v.Id + ".png" +
                                  (args.Contains("--keep-use") ? "" : "; " + v.Tier + " now uses " + v.Id));
                Console.WriteLine("Next: refresh Unity (ArtImport rebinds), run EditMode \"Art:" + spec.Id + "\", then ./Tools/test-gate.sh art shots (pictures + pages)");
                return 0;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is IOException)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }

        public static int Versions(string root, string[] args)
        {
            var spec = args.Length > 2 ? Spec(root, args[2]) : null;
            if (spec == null)
                return args.Length > 2 ? 64 : Usage("art versions <slot>");
            var log = ArtVersions.Load(root, spec.Id);
            if (log.Versions.Count == 0)
            {
                Console.WriteLine(spec.Id + ": no versions yet (graybox). Add one: ./Tools/test-gate.sh art add " + spec.Id + " draft.svg --tier candidate --by claude");
                return 0;
            }
            var approved = AcceptedVersions(root, spec.Id);
            foreach (var v in log.Versions.OrderBy(v => v.Number))
            {
                var uses = log.Use.Where(u => u.Value == v.Id).Select(u => "uses:" + u.Key);
                Console.WriteLine(v.Id + "  " + v.Date + "  " + v.Tier.PadRight(9) + " " + v.By.PadRight(6) + " from " + v.Parent.PadRight(4) + "  " +
                                  string.Join(" ", uses.Concat(approved.Contains(v.Id) ? new[] { "accepted" } : new string[0])) + (v.Note.Length > 0 ? "  \"" + v.Note + "\"" : ""));
            }
            return 0;
        }

        public static int Use(string root, string[] args)
        {
            if (args.Length < 4)
                return Usage("art use <slot> <vNNN> [--as candidate|final]");
            var spec = Spec(root, args[2]);
            if (spec == null)
                return 64;
            try
            {
                string tier = Opt(args, "--as");
                string previous = ArtVersions.Use(root, spec.Id, args[3], tier);
                Console.WriteLine(spec.Id + " " + (tier ?? ArtVersions.Load(root, spec.Id).Find(args[3]).Tier) + ": " + (previous ?? "(none)") + " → " + args[3] +
                                  ". Refresh Unity so ArtImport rebinds it.");
                return 0;
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine(e.Message);
                return 1;
            }
        }

        public static int Codex(string root, string[] args)
        {
            var spec = args.Length > 2 ? Spec(root, args[2]) : null;
            if (spec == null)
                return args.Length > 2 ? 64 : Usage("art codex <slot> [--from vNNN] [--dry-run]");
            var log = ArtVersions.Load(root, spec.Id);
            string from = Opt(args, "--from") ?? (log.Use.TryGetValue("candidate", out var c) ? c : null);
            if (from == null || log.Find(from) == null)
            {
                Console.Error.WriteLine(spec.Id + ": no reference version (add a candidate first, or pass --from vNNN)");
                return 1;
            }
            var (feature, slot) = ArtStyle.Load(root, spec);
            string work = Layout.Full(root, ".scratch/art/" + spec.Id + "/");
            string reference = Layout.Full(root, ArtVersions.Dir(spec.Id) + from + ".png");
            string prompt = ArtCodexRun.Prompt(spec, feature, slot, from);
            var codexArgs = ArtCodexRun.Args(work, reference);
            if (args.Contains("--dry-run"))
            {
                Console.WriteLine("codex " + string.Join(" ", codexArgs.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)) + "   (prompt on stdin)\n\n" + prompt);
                return 0;
            }
            Directory.CreateDirectory(work);
            string output = Path.Combine(work, ArtCodexRun.OutputFile);
            if (File.Exists(output))
                File.Delete(output);
            Console.WriteLine("Running Codex for " + spec.Id + " from " + from + " (can take minutes)...");
            var psi = new ProcessStartInfo("codex") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in codexArgs)
                psi.ArgumentList.Add(a);
            using (var p = Process.Start(psi))
            {
                p.StandardInput.Write(prompt);
                p.StandardInput.Close();
                string log2 = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                if (!p.WaitForExit(30 * 60 * 1000))
                {
                    p.Kill(true);
                    Console.Error.WriteLine("Codex timed out after 30 min.");
                    return 1;
                }
                File.WriteAllText(Path.Combine(work, "codex.log"), log2);
                if (p.ExitCode != 0 || !File.Exists(output))
                {
                    Console.Error.WriteLine("Codex did not produce " + ArtCodexRun.OutputFile + " (exit " + p.ExitCode + "); log: .scratch/art/" + spec.Id + "/codex.log");
                    return 1;
                }
            }
            var v = ArtVersions.Add(root, spec.Id, output, "final", "codex", "from " + from, from, Today(), Inkscape);
            Console.WriteLine(spec.Id + " " + v.Id + " (final, codex, from " + from + "); final now uses " + v.Id + ". Refresh Unity, run the art gate, then compare.");
            return 0;
        }

        /// <summary>SVG → PNG at the viewBox size (1 user unit = 1 px). Error text, or null.</summary>
        public static string Inkscape(string svg, string png)
        {
            try
            {
                var psi = new ProcessStartInfo("inkscape") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in new[] { svg, "--export-type=png", "--export-filename=" + png, "--export-background-opacity=0" })
                    psi.ArgumentList.Add(a);
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0 ? null : "inkscape exit " + p.ExitCode + ": " + err.Trim();
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return "inkscape not found: install it, or add a PNG instead of an SVG";
            }
        }

        public static HashSet<string> AcceptedVersions(string root, string slotId)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            string p = Layout.Full(root, ArtStatus.AcceptedPath);
            if (File.Exists(p))
                foreach (var line in File.ReadAllLines(p))
                {
                    var f = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 5 && f[0] == slotId && f[4].StartsWith("v", StringComparison.Ordinal) && f[3].IndexOf(".candidate.", StringComparison.Ordinal) < 0) // finals only
                        set.Add(f[4]);
                }
            return set;
        }

        private static ArtSlotSpec Spec(string root, string id)
        {
            var specs = ArtBriefCli.LoadSpecs(root);
            var s = specs?.FirstOrDefault(x => x.Id == id);
            if (s == null && specs != null)
                Console.Error.WriteLine("unknown slot '" + id + "' (known: " + string.Join(", ", specs.Select(x => x.Id)) + ")");
            return s;
        }

        private static string Opt(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static int Usage(string text)
        {
            Console.Error.WriteLine("Usage: " + text);
            return 64;
        }
    }

    /// <summary>The Codex CLI run for a FINAL version (pure: prompt and arguments; the CLI runs it).</summary>
    public static class ArtCodexRun
    {
        public const string OutputFile = "final.png";

        public static string Prompt(ArtSlotSpec s, string featureStyle, string slotStyle, string fromVersion) =>
            "Generate FINAL production art with your image generation tool (image_gen). Do not draw it with code, do not cut or trace the reference.\n" +
            "The attached image is the approved design (version " + fromVersion + "): keep its subject, silhouette, proportions, pose, colours and anchor; " +
            "raise the rendering quality (clean shapes, consistent lighting, crisp edges at small size).\n" +
            "Save exactly one PNG with real transparency as ./" + OutputFile + " in the working directory, then stop.\n\n" +
            ArtBrief.Prompt(s, featureStyle, slotStyle);

        /// <summary>Same shape as the popup-reskin-codex pipeline: workspace-write sandbox in a scratch folder, reference via -i.</summary>
        public static List<string> Args(string workDir, string referencePng) => new List<string>
        {
            "exec", "-C", workDir, "-s", "workspace-write", "--skip-git-repo-check",
            "--add-dir", Path.GetDirectoryName(referencePng), "-i", referencePng, "-o", Path.Combine(workDir, "last.txt"), "-"
        };
    }
}
