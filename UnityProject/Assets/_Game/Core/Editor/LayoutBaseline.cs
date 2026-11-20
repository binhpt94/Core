using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Core.DevTools;
using UnityEngine;

namespace Game.Core.EditorTools
{
    /// <summary>
    /// L3: the text snapshot of a view (all fixtures × devices) compared with its committed baseline.
    /// Baselines are owned like golden replays: LAYOUT_WRITE=1 (owners only, denied to agents) rewrites them,
    /// the reviewer reads the diff ("Background/Question: 60,520 960x400 -> 60,519 960x402").
    /// A missing baseline is UNKNOWN, never PASS.
    /// </summary>
    public static class LayoutBaseline
    {
        public const string WriteVariable = "LAYOUT_WRITE";

        public static string ProjectRoot => Path.GetDirectoryName(Application.dataPath);

        public static bool WriteRequested => Environment.GetEnvironmentVariable(WriteVariable) == "1";

        public static string Render(IViewCases cases)
        {
            var layouts = new List<LayoutCase>();
            foreach (var fixture in cases.SnapshotFixtureNames)
                foreach (var device in cases.Devices)
                    using (var stage = ViewStage.Open(cases, fixture, device))
                        layouts.Add(stage.Layout);
            return LayoutSnapshot.Render(cases.ViewId, layouts);
        }

        /// <summary>Empty list = matches. Writes the baseline instead when LAYOUT_WRITE=1.</summary>
        public static List<string> Compare(IViewCases cases, string actual)
        {
            string path = Path.Combine(ProjectRoot, cases.BaselinePath);
            if (WriteRequested)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, actual.Replace("\r\n", "\n"));
                return new List<string>();
            }
            if (!File.Exists(path))
                return new List<string> { "UNKNOWN: no baseline " + cases.BaselinePath + " (run the layout gate once with " + WriteVariable + "=1, alone: the owner answers yes/no; then commit it)" };
            return LayoutSnapshot.Diff(File.ReadAllText(path), actual).Select(d => cases.ViewId + " " + d).ToList();
        }
    }
}
