using Game.Progress.Infrastructure;
using UnityEditor;
using UnityEngine;

namespace Game.Bootstrap.EditorTools
{
    /// <summary>Editor shortcuts so nobody (human or AI) has to hand-edit scene files.</summary>
    public static class GameMenu
    {
        [MenuItem("Tools/Game/Create Game Root In Scene")]
        public static void CreateGameRoot()
        {
#if UNITY_2023_1_OR_NEWER
            var existing = Object.FindFirstObjectByType<GameCompositionRoot>();
#else
            var existing = Object.FindObjectOfType<GameCompositionRoot>();
#endif
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                Debug.Log("[Game] GameCompositionRoot already exists in the scene.");
                return;
            }

            var go = new GameObject("GameRoot");
            go.AddComponent<GameCompositionRoot>();
            Undo.RegisterCreatedObjectUndo(go, "Create Game Root");
            Selection.activeGameObject = go;
        }

        /// <summary>Saves the running session's replay (seed + commands + hashes) under Replays/ in the project.</summary>
        [MenuItem("Tools/Game/Debug/Save Replay Of Current Session")]
        public static void SaveReplay()
        {
#if UNITY_2023_1_OR_NEWER
            var root = Object.FindFirstObjectByType<GameCompositionRoot>();
#else
            var root = Object.FindObjectOfType<GameCompositionRoot>();
#endif
            if (!Application.isPlaying || root == null)
            {
                Debug.LogWarning("[Game] Enter Play Mode first.");
                return;
            }
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "Replays");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, System.DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".replay.txt");
            System.IO.File.WriteAllText(path, root.ExportReplay());
            Debug.Log("[Game] Replay saved: " + path + ". Reproduce: copy into Assets/_Game/Features/MathQuiz/Tests/Replays/ and run dotnet test.");
        }

        [MenuItem("Tools/Game/Reset Saved Progress")]
        public static void ResetProgress()
        {
            PlayerPrefs.DeleteKey(ProgressInstaller.PlayerPrefsKey);
            PlayerPrefs.Save();
            Debug.Log("[Game] Saved progress cleared.");
        }
    }
}
