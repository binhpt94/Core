using System;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;

/// <summary>
/// `test-gate.sh graph ...`: read the architecture without reading code. Output is compact text
/// (a few hundred tokens), never the whole graph.
/// </summary>
internal static class GraphCommands
{
    public static int Run(string root, string[] args, Func<string, string, (int code, string output)> exec)
    {
        string sub = args.Length > 1 ? args[1] : "summary";
        var graph = ArchitectureGraph.Build(root);

        switch (sub)
        {
            case "--write":
            {
                var changed = ArchitectureDocs.WriteAll(root);
                Console.WriteLine(changed.Count == 0 ? "generated docs already up to date" : "wrote " + string.Join(", ", changed));
                var errors = ArchitectureGraph.Build(root).ParseErrors;
                if (errors.Count > 0)
                {
                    Console.WriteLine("but the generator could not read everything (fix these, the diagrams would be incomplete):");
                    Console.WriteLine(ArchitectureScanner.Format(errors));
                    return 1;
                }
                return 0;
            }
            case "summary":
                foreach (var e in graph.Edges)
                    Console.WriteLine(e);
                return 0;
            case "feature":
                return Need(args, out var f) ? Print(graph.DescribeFeature(f)) : 64;
            case "impact":
                if (!Need(args, out var name))
                    return 64;
                var impacted = graph.Impact(name);
                Console.WriteLine(name + " impact: " + (impacted.Count == 0 ? "(nothing depends on it)" : string.Join(", ", impacted)));
                return 0;
            case "di":
                return Need(args, out var feature) ? Print(ArchitectureGraph.DescribeDependencyInjection(root, feature)) : 64;
            case "diff":
            {
                string baseRef = args.Length > 2 ? args[2] : "HEAD";
                var (code, list) = exec("git", "ls-tree -r --name-only " + baseRef + " -- " + ArchitectureDocs.Dir);
                var old = new System.Text.StringBuilder();
                if (code == 0)
                    foreach (var path in list.Split('\n').Select(l => l.Trim()).Where(l => l.EndsWith(".md", StringComparison.Ordinal)))
                        old.Append(exec("git", "show " + baseRef + ":./" + path).output).Append('\n');
                else
                    Console.WriteLine("no committed system map at " + baseRef + "; showing all edges as new");

                var before = Edges(old.ToString());
                var after = Edges(string.Join("\n", ArchitectureDocs.Render(graph, int.MaxValue).Values));
                var added = after.Except(before).ToList();
                var removed = before.Except(after).ToList();
                if (added.Count == 0 && removed.Count == 0)
                    Console.WriteLine("architecture unchanged vs " + baseRef);
                foreach (var e in removed) Console.WriteLine("- " + e);
                foreach (var e in added) Console.WriteLine("+ " + e);
                return 0;
            }
            default:
                Console.Error.WriteLine("Usage: graph [--write | summary | feature <F> | impact <F> | di <F> | diff [base]]");
                return 64;
        }
    }

    private static string[] Edges(string overview) =>
        overview.Split('\n').Select(l => l.Trim()).Where(l => l.Contains("-->|") || l.Contains("-.->|")).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToArray();

    private static bool Need(string[] args, out string value)
    {
        value = args.Length > 2 ? args[2] : null;
        if (value == null)
            Console.Error.WriteLine("missing feature name");
        return value != null;
    }

    private static int Print(string text)
    {
        Console.WriteLine(text);
        return 0;
    }
}
