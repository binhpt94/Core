using Game.Core;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Bootstrap.Tests.Validation
{
    /// <summary>Test component: one required field, one optional, one required array.</summary>
    public sealed class SampleView : MonoBehaviour
    {
        [SerializeField] private Text title;
        [OptionalReference] [SerializeField] private Text subtitle = null;
        [SerializeField] private Button[] buttons = new Button[1];

        internal Text Subtitle => subtitle;

        internal void Fill(Text t, Button b)
        {
            title = t;
            buttons[0] = b;
        }
    }
}
