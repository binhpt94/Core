using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;

namespace TestGate.Planning
{
    /// <summary>
    ///   art brief [SLOT-ID|--all] [--print]   generate the brief (.md, for the human) + prompt (.prompt.txt, for an image model) per slot
    ///                                 into Dashboard/Data/art/briefs/, from the REAL contracts + the feature's *.style.md;
    ///                                 --print also writes both texts to stdout (agents read them there, not from Dashboard/Data)
    ///                                 + &lt;slot&gt;.compare.html: the levels side by side with the contract drawn over them,
    ///                                 and the views on every device (judge by eye; a note box per picture → `art note` command)
    ///   art shots                     take the pictures (Unity batchmode with a GPU, Editor closed) + regenerate all pages
    ///   art note SLOT-ID "why"        record why a version was rejected (appended to the slot's style file; every later
    ///                                 brief and prompt carries it, so the same feedback is never given twice)
    /// </summary>
    public static class ArtBriefCli
    {
        public const string BriefDir = "Dashboard/Data/art/briefs/";
        public const string ShotDir = "Dashboard/Data/art/shots/";

        public static int Brief(string root, string[] args)
        {
            var specs = LoadSpecs(root);
            if (specs == null)
                return 1;
            bool print = args.Contains("--print");
            string want = args.Skip(2).FirstOrDefault(a => a != "--print") ?? "--all";
            var chosen = want == "--all" ? specs : specs.Where(s => s.Id == want).ToList();
            if (chosen.Count == 0)
            {
                Console.Error.WriteLine("unknown slot '" + want + "' (known: " + string.Join(", ", specs.Select(s => s.Id)) + ")");
                return 64;
            }
            var levels = ArtStatus.Compute(root);
            Directory.CreateDirectory(Layout.Full(root, BriefDir));
            foreach (var s in chosen)
            {
                var (feature, slot) = ArtStyle.Load(root, s);
                string level = levels.TryGetValue(s.Id, out var st) ? st.Level : null;
                var shots = Shots(root, s.Id);
                string md = ArtBrief.Markdown(s, feature, slot, level, shots), prompt = ArtBrief.Prompt(s, feature, slot);
                File.WriteAllText(Layout.Full(root, BriefDir + s.Id + ".md"), md);
                File.WriteAllText(Layout.Full(root, BriefDir + s.Id + ".prompt.txt"), prompt);
                File.WriteAllText(Layout.Full(root, BriefDir + s.Id + ".compare.html"), ArtCompare.Html(s, level, shots, ArtStyle.Rejections(slot), Sidecars(root, shots), VersionCards(root, s.Id)));
                Console.WriteLine(s.Id.PadRight(22) + BriefDir + s.Id + ".md · .prompt.txt · .compare.html" + (feature.Length == 0 ? "  (no " + ArtStyle.FeaturePath(s.Feature) + ")" : ""));
                if (print)
                    Console.WriteLine("\n" + md + "\n----- prompt -----\n" + prompt);
            }
            return 0;
        }

        /// <summary>
        /// `art shots`: takes the pictures with Unity in batchmode WITH a graphics device (the Editor must be closed; with it open,
        /// use Tools > Game > Art > Snapshot), then regenerates every brief and compare page.
        /// </summary>
        public static int ShotsCommand(string root, string[] args)
        {
            string unity = UnityEditorPath(root);
            if (unity == null)
            {
                Console.Error.WriteLine("Unity not found: set UNITY_PATH, or install the project's version in Unity Hub.");
                return 1;
            }
            string log = Layout.Full(root, ".scratch/art/shots.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            Console.WriteLine("Taking art pictures with Unity (batchmode, GPU; about a minute)...");
            var psi = new ProcessStartInfo(unity) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-batchmode", "-projectPath", Layout.Full(root, "UnityProject"), "-executeMethod",
                         "Game.Core.EditorTools.ArtSnapshot.CaptureFromCommandLine", "-logFile", log })
                psi.ArgumentList.Add(a);
            int code;
            using (var p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                p.WaitForExit();
                code = p.ExitCode;
            }
            string text = File.Exists(log) ? File.ReadAllText(log) : "";
            if (text.Contains("another Unity instance is running"))
            {
                Console.Error.WriteLine("The Unity Editor has this project open: use Tools > Game > Art > Snapshot there, then ./Tools/test-gate.sh art compare --all");
                return 1;
            }
            if (code != 0)
            {
                foreach (var line in text.Split('\n').Where(l => l.Contains("[ArtSnapshot]") || l.Contains("error CS")).Take(10))
                    Console.Error.WriteLine(line.Trim());
                Console.Error.WriteLine("Unity exit " + code + "; full log: .scratch/art/shots.log");
                return 1;
            }
            Console.WriteLine(text.Split('\n').FirstOrDefault(l => l.Contains("[ArtSnapshot]") && l.Contains("pictures"))?.Trim() ?? "pictures written");
            return Brief(root, new[] { "art", "brief", "--all" });
        }

        /// <summary>UNITY_PATH, else the Unity Hub install of the project's editor version (ProjectSettings/ProjectVersion.txt).</summary>
        public static string UnityEditorPath(string root)
        {
            string env = Environment.GetEnvironmentVariable("UNITY_PATH");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                return env;
            string pv = Layout.Full(root, "UnityProject/ProjectSettings/ProjectVersion.txt");
            if (!File.Exists(pv))
                return null;
            string version = File.ReadAllLines(pv).Where(l => l.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                .Select(l => l.Substring("m_EditorVersion:".Length).Trim()).FirstOrDefault();
            if (string.IsNullOrEmpty(version))
                return null;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return new[]
            {
                Path.Combine(home, "Unity", "Hub", "Editor", version, "Editor", "Unity"),
                "/Applications/Unity/Hub/Editor/" + version + "/Unity.app/Contents/MacOS/Unity",
                "C:/Program Files/Unity/Hub/Editor/" + version + "/Editor/Unity.exe"
            }.FirstOrDefault(File.Exists);
        }

        public static int Note(string root, string[] args)
        {
            if (args.Length < 4 || string.IsNullOrWhiteSpace(args[3]))
            {
                Console.Error.WriteLine("Usage: art note <slot id> \"<why this version was rejected>\"");
                return 64;
            }
            var specs = LoadSpecs(root);
            if (specs == null)
                return 1;
            var s = specs.FirstOrDefault(x => x.Id == args[2]);
            if (s == null)
            {
                Console.Error.WriteLine("unknown slot '" + args[2] + "' (known: " + string.Join(", ", specs.Select(x => x.Id)) + ")");
                return 64;
            }
            string rel = ArtStyle.AppendRejection(root, s, args[3], DateTime.UtcNow.ToString("yyyy-MM-dd"));
            Console.WriteLine("Noted in " + rel + ". Regenerate: ./Tools/test-gate.sh art brief " + s.Id);
            return 0;
        }

        /// <summary>Shots written by the Unity snapshot (Dashboard/Data/art/shots/: &lt;slot&gt;.&lt;level&gt;.png, &lt;slot&gt;.inview.&lt;device&gt;.png), relative to the brief.</summary>
        public static List<string> Shots(string root, string id)
        {
            string dir = Layout.Full(root, ShotDir);
            if (!Directory.Exists(dir))
                return new List<string>();
            // The slot alone at each level, then the slot in the game on every device (ArtSnapshot).
            return Directory.EnumerateFiles(dir, id + ".*.png").Select(Path.GetFileName)
                .OrderBy(f => f.Contains(".inview.")).ThenBy(f => f, StringComparer.Ordinal).Select(f => "../shots/" + f).ToList();
        }

        public const string VersionCopyDir = "Dashboard/Data/art/versions/";

        /// <summary>
        /// The slot's versions for the compare page. PNGs are copied next to the dashboard (Dashboard/Data/art/versions/&lt;slot&gt;/)
        /// so the page works from file:// and from `serve` alike; the version store in Assets stays the source.
        /// </summary>
        public static List<VersionCard> VersionCards(string root, string id)
        {
            var log = ArtVersions.Load(root, id);
            var approved = ArtVersionCli.AcceptedVersions(root, id);
            var cards = new List<VersionCard>();
            string copyDir = Layout.Full(root, VersionCopyDir + id + "/");
            foreach (var v in log.Versions)
            {
                string png = Layout.Full(root, ArtVersions.Dir(id) + v.Id + ".png");
                if (!File.Exists(png))
                    continue;
                Directory.CreateDirectory(copyDir);
                File.Copy(png, Path.Combine(copyDir, v.Id + ".png"), true);
                var (w, h) = VersionCard.PngSize(File.ReadAllBytes(png));
                var card = new VersionCard { Version = v, Src = "../versions/" + id + "/" + v.Id + ".png", Width = w, Height = h, Approved = approved.Contains(v.Id) };
                card.Uses.AddRange(log.Use.Where(u => u.Value == v.Id).Select(u => u.Key));
                cards.Add(card);
            }
            return cards;
        }

        /// <summary>In-game shot → its sidecar text (where the slot sits), when ArtSnapshot wrote one.</summary>
        public static Dictionary<string, string> Sidecars(string root, IEnumerable<string> shots)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var shot in shots.Where(f => f.Contains(".inview.")))
            {
                string txt = Layout.Full(root, ShotDir + Path.GetFileNameWithoutExtension(shot) + ".txt");
                if (File.Exists(txt))
                    map[shot] = File.ReadAllText(txt);
            }
            return map;
        }

        /// <summary>The exported contracts; re-exported (pure test, seconds) when missing or older than any contract source.</summary>
        public static List<ArtSlotSpec> LoadSpecs(string root)
        {
            string path = Layout.Full(root, ArtSlotSpecs.Path);
            if (IsStale(root, path))
            {
                Console.WriteLine("Exporting art contracts (dotnet test Tools/LogicTests --filter ArtSlotExport)...");
                var psi = new ProcessStartInfo("dotnet", "test Tools/LogicTests --filter FullyQualifiedName~ArtSlotExport -v q --nologo")
                { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0 || !File.Exists(path))
                    {
                        Console.Error.WriteLine("Could not export the art contracts:\n" + output.Trim());
                        return null;
                    }
                }
            }
            return ArtSlotSpecs.Parse(File.ReadAllText(path));
        }

        private static bool IsStale(string root, string exported)
        {
            if (!File.Exists(exported))
                return true;
            var at = File.GetLastWriteTimeUtc(exported);
            string game = Layout.Full(root, Layout.Unity + "Assets/_Game");
            var sources = Directory.EnumerateFiles(Path.Combine(game, "Features"), "*ArtSlots.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(Path.Combine(game, "Core", "Runtime"), "ArtSlotContract.cs", SearchOption.AllDirectories));
            return sources.Any(f => File.GetLastWriteTimeUtc(f) > at);
        }
    }
}
