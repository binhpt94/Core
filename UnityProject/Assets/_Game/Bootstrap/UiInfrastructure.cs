using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.Bootstrap
{
    /// <summary>
    /// App-level UI infrastructure. Features never search the scene for it (ARCH043); Bootstrap owns it.
    /// </summary>
    public static class UiInfrastructure
    {
        public static void EnsureEventSystem(Transform parent)
        {
#if UNITY_2023_1_OR_NEWER
            if (Object.FindFirstObjectByType<EventSystem>() != null)
                return;
#else
            if (Object.FindObjectOfType<EventSystem>() != null)
                return;
#endif
            var go = new GameObject("EventSystem", typeof(EventSystem));
            go.transform.SetParent(parent, false);
#if GAME_HAS_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
            go.AddComponent<StandaloneInputModule>();
#endif
        }
    }
}
