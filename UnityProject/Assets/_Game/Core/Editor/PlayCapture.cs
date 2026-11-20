using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Game.Core.EditorTools
{
    /// <summary>
    /// "Does it look right IN THE GAME?" from the command line, with the Editor's UI (NOT -batchmode: the Game view must exist):
    ///   Unity -projectPath UnityProject -executeMethod Game.Core.EditorTools.PlayCapture.FromCommandLine
    ///         -captureScene Assets/_Game/Scenes/Demo.unity -captureSeconds 5 -captureOut /abs/path/shot.png
    /// Opens the scene, enters Play Mode, waits, screenshots the Game view, exits (code 0 = written, 1 = failed).
    /// State survives the Play Mode domain reload through SessionState. Never touches scene or prefab files.
    /// </summary>
    [InitializeOnLoad]
    public static class PlayCapture
    {
        private const string OutKey = "PlayCapture.Out", SecondsKey = "PlayCapture.Seconds", StartKey = "PlayCapture.Start", AskedKey = "PlayCapture.Asked", RequestedKey = "PlayCapture.Requested";

        static PlayCapture()
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(OutKey, "")))
                EditorApplication.update += Tick;
        }

        public static void FromCommandLine()
        {
            string scene = Arg("-captureScene"), output = Arg("-captureOut");
            if (scene == null || output == null || !File.Exists(scene))
            {
                Debug.LogError("[PlayCapture] usage: -captureScene Assets/....unity -captureOut /abs/out.png [-captureSeconds 5] (scene: " + scene + ")");
                EditorApplication.Exit(1);
                return;
            }
            float seconds = float.TryParse(Arg("-captureSeconds"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 5f;
            if (File.Exists(output))
                File.Delete(output);
            SessionState.SetString(OutKey, output);
            SessionState.SetFloat(SecondsKey, seconds);
            SessionState.SetFloat(StartKey, -1f);
            SessionState.SetFloat(RequestedKey, (float)EditorApplication.timeSinceStartup);
            EditorSceneManager.OpenScene(scene);
            EditorApplication.update += Tick;
            EditorApplication.isPlaying = true;
        }

        private static void Tick()
        {
            string output = SessionState.GetString(OutKey, "");
            if (output.Length == 0)
                return;
            if (!EditorApplication.isPlaying)
            {
                // Play Mode can be refused (e.g. the validation gate blocks it on rule violations): fail instead of hanging.
                if (!EditorApplication.isPlayingOrWillChangePlaymode && SessionState.GetFloat(StartKey, -1f) < 0f &&
                    EditorApplication.timeSinceStartup - SessionState.GetFloat(RequestedKey, 0f) > 90f)
                {
                    Debug.LogError("[PlayCapture] FAILED: Play Mode did not start (blocked? see the Console above)");
                    SessionState.EraseString(OutKey);
                    EditorApplication.update -= Tick;
                    EditorApplication.Exit(1);
                }
                return;
            }
            float start = SessionState.GetFloat(StartKey, -1f);
            float now = (float)EditorApplication.timeSinceStartup;
            if (start < 0f)
            {
                SessionState.SetFloat(StartKey, now);
                return;
            }
            float waited = now - start, seconds = SessionState.GetFloat(SecondsKey, 5f);
            if (waited >= seconds && !File.Exists(output) && SessionState.GetFloat(AskedKey, 0f) == 0f)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                ScreenCapture.CaptureScreenshot(output);
                SessionState.SetFloat(AskedKey, now);
                Debug.Log("[PlayCapture] screenshot requested after " + waited.ToString("0.0") + " s");
            }
            float asked = SessionState.GetFloat(AskedKey, 0f);
            if (asked > 0f && (File.Exists(output) || now - asked > 10f))
            {
                bool ok = File.Exists(output);
                Debug.Log(ok ? "[PlayCapture] wrote " + output : "[PlayCapture] FAILED: no screenshot written");
                SessionState.EraseString(OutKey);
                SessionState.EraseFloat(AskedKey);
                EditorApplication.update -= Tick;
                EditorApplication.Exit(ok ? 0 : 1);
            }
        }

        private static string Arg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
