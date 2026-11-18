using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Game.Bootstrap.EditorTools
{
    /// <summary>
    /// Tools > Game > Open Dashboard: the same as `./Tools/test-gate.sh dash`, then opens Dashboard/index.html.
    /// Calls the prebuilt TestGate runner through `dotnet` directly, so it needs no bash on Windows.
    /// </summary>
    public static class DashboardMenu
    {
        private const string GateDll = "Tools/TestGate/bin/Release/net8.0/TestGate.dll";
        private const string DashboardPage = "Dashboard/index.html";

        [MenuItem("Tools/Game/Open Dashboard")]
        public static void OpenDashboard()
        {
            string root = Game.Architecture.Rules.Layout.RootFromDataPath(Application.dataPath);
            try
            {
                if (!File.Exists(Path.Combine(root, GateDll)))
                {
                    EditorUtility.DisplayProgressBar("Dashboard", "Building Tools/TestGate (first run)...", 0.3f);
                    if (!RunDotnet(root, "build Tools/TestGate -c Release -v q --nologo"))
                        return;
                }
                EditorUtility.DisplayProgressBar("Dashboard", "Writing Dashboard/Data/ ...", 0.7f);
                if (!RunDotnet(root, "\"" + GateDll + "\" dash"))
                    return;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            string page = Path.Combine(root, DashboardPage).Replace('\\', '/');
            Application.OpenURL("file:///" + page.TrimStart('/'));
            Debug.Log("[Game] Dashboard opened: " + page);
        }

        private static bool RunDotnet(string root, string args)
        {
            var psi = new ProcessStartInfo("dotnet", args)
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode == 0)
                        return true;
                    Debug.LogError("[Game] dotnet " + args + " failed (exit " + p.ExitCode + "):\n" + output + error);
                    return false;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Debug.LogError("[Game] 'dotnet' not found on PATH: install the .NET 8 SDK, or run ./Tools/test-gate.sh dash in Git Bash.");
                return false;
            }
        }
    }
}
