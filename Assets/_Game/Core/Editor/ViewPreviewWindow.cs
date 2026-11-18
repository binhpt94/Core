using System.Collections.Generic;
using System.Linq;
using Game.Core.DevTools;
using UnityEditor;
using UnityEngine;

namespace Game.Core.EditorTools
{
    /// <summary>
    /// "Check one view" without running the game: pick view × fixture (incl. scenarios) × device, look at it in
    /// the Scene view, read the constraint violations. Same fixtures and checks as the gate, so what you see is
    /// what the gate judges. Tools > Game > UI > View Preview.
    /// </summary>
    internal sealed class ViewPreviewWindow : EditorWindow
    {
        private IReadOnlyList<IViewCases> _cases;
        private int _view, _fixture, _device;
        private ViewStage _stage;
        private List<LayoutViolation> _violations = new List<LayoutViolation>();
        private string _error;
        private Vector2 _scroll;

        [MenuItem("Tools/Game/UI/View Preview")]
        private static void Open() => GetWindow<ViewPreviewWindow>("View Preview");

        private void OnEnable() => _cases = ViewCasesRegistry.All();

        private void OnDisable() => Close(ref _stage);

        private void OnGUI()
        {
            if (_cases == null || _cases.Count == 0)
            {
                EditorGUILayout.HelpBox("No view cases found (Features/<F>/Editor/Views/<View>Cases.cs).", MessageType.Info);
                return;
            }

            _view = EditorGUILayout.Popup("View", Mathf.Min(_view, _cases.Count - 1), _cases.Select(c => c.ViewId).ToArray());
            var cases = _cases[_view];
            var fixtures = cases.FixtureNames;
            var devices = cases.Devices;
            _fixture = EditorGUILayout.Popup("Fixture", Mathf.Min(_fixture, fixtures.Count - 1), fixtures.ToArray());
            _device = EditorGUILayout.Popup("Device", Mathf.Min(_device, devices.Count - 1), devices.Select(d => d.Name).ToArray());

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Show"))
                    Show(cases, fixtures[_fixture], devices[_device]);
                if (GUILayout.Button("Clear"))
                    Close(ref _stage);
                if (GUILayout.Button("Copy snapshot") && _stage != null)
                    EditorGUIUtility.systemCopyBuffer = LayoutSnapshot.Render(cases.ViewId, new[] { _stage.Layout });
            }

            if (_error != null)
                EditorGUILayout.HelpBox(_error, MessageType.Error);
            else if (_stage != null)
                EditorGUILayout.HelpBox(_violations.Count == 0 ? "Layout OK" : _violations.Count + " violation(s)", _violations.Count == 0 ? MessageType.Info : MessageType.Warning);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var v in _violations)
                EditorGUILayout.SelectableLabel(v.ToString(), GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndScrollView();
        }

        private void Show(IViewCases cases, string fixture, DeviceProfile device)
        {
            Close(ref _stage);
            _error = null;
            try
            {
                // Visible in the Scene view, but never saved into the open scene.
                _stage = ViewStage.Open(cases, fixture, device, hidden: false);
                foreach (var t in _stage.Root.GetComponentsInChildren<Transform>(true))
                    t.gameObject.hideFlags = HideFlags.DontSave;
                _violations = _stage.Check(cases.Limits);
                Selection.activeGameObject = _stage.Root;
                var sceneView = SceneView.lastActiveSceneView; // no ?. on UnityEngine.Object
                if (sceneView != null)
                    sceneView.FrameSelected();
            }
            catch (System.Exception e)
            {
                _violations = new List<LayoutViolation>();
                _error = e.GetType().Name + ": " + e.Message;
            }
        }

        private static void Close(ref ViewStage stage)
        {
            stage?.Dispose();
            stage = null;
        }
    }
}
