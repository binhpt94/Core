using System;

namespace Game.Core
{
    /// <summary>
    /// Every [SerializeField] object reference on a Game.* component is REQUIRED by default:
    /// the Unity validators (ARCH072: on import, before Play Mode, before build) report it when empty.
    /// Mark the rare field that may legitimately stay empty with this attribute.
    /// Required-by-default means forgetting an attribute can never hide a missing reference.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true)]
    public sealed class OptionalReferenceAttribute : Attribute
    {
    }

    /// <summary>
    /// Allows a serialized primitive (int/float/bool/string) on a feature MonoBehaviour (ARCH070)
    /// because it is purely presentational (animation duration, padding...). Gameplay config belongs
    /// in the feature's ScriptableObject, never on a MonoBehaviour.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = true)]
    public sealed class VisualTuningAttribute : Attribute
    {
    }
}
