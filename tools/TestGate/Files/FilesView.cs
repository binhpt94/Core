using System.Collections.Generic;
using System.Linq;
using Game.Architecture.Rules;

namespace TestGate.Files
{
    /// <summary>Dashboard data for files: all derived on each render (git + event log + paths + GUID graph).</summary>
    public sealed class FilesView
    {
        public List<LooseFile> Loose { get; set; } = new List<LooseFile>();
        public List<UnusedAsset> Unused { get; set; } = new List<UnusedAsset>();
        public int UnusedCount { get; set; }
        public List<DiskUse> Disk { get; set; } = new List<DiskUse>();
        /// <summary>ARCH130/131/132 lines (layout, junk, broken GUID references).</summary>
        public List<string> Violations { get; set; } = new List<string>();

        public static FilesView Build(string root, IReadOnlyList<TestGate.AgentControl.AgentEvent> events = null)
        {
            var unused = FilesReport.Unused(root);
            var loose = FilesReport.Loose(root, events);
            var v = FileRules.Check(root).Select(x => x.ToString()).ToList();
            v.AddRange(loose.SelectMany(f => FileRules.CheckPath(f.Path)).Select(x => x.ToString()));
            return new FilesView
            {
                Loose = loose, Unused = unused.Take(30).ToList(), UnusedCount = unused.Count, Disk = FilesReport.Du(root),
                Violations = v.Distinct().OrderBy(x => x, System.StringComparer.Ordinal).ToList()
            };
        }
    }
}
