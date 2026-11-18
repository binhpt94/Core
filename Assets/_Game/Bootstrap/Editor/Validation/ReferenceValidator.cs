using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Core;
using UnityEditor;
using UnityEngine;

namespace Game.Bootstrap.EditorTools
{
    /// <summary>
    /// ARCH072 / ARCH073 on real Unity objects: every serialized object reference on a Game.* component
    /// is REQUIRED unless the field has [OptionalReference]; null components are missing scripts.
    /// Required-by-default: an agent that forgets an attribute gets an error, not a silent hole.
    /// </summary>
    public static class ReferenceValidator
    {
        public static List<string> ValidateHierarchy(GameObject root, string context)
        {
            var errors = new List<string>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                foreach (var component in t.GetComponents<Component>())
                {
                    if (component == null)
                    {
                        errors.Add("ARCH073 " + context + " " + PathOf(t) + ": missing script (remove the component or restore the script with its .meta)");
                        continue;
                    }
                    if (component is MonoBehaviour behaviour)
                        errors.AddRange(ValidateObject(behaviour, context + " " + PathOf(t)));
                }
            }
            return errors;
        }

        /// <summary>Checks one MonoBehaviour or ScriptableObject of a Game.* type.</summary>
        public static IEnumerable<string> ValidateObject(UnityEngine.Object target, string context)
        {
            var type = target.GetType();
            if (type.Namespace == null || !type.Namespace.StartsWith("Game.", StringComparison.Ordinal))
                yield break;

            var so = new SerializedObject(target);
            var it = so.GetIterator();
            bool enterChildren = true;
            while (it.NextVisible(enterChildren))
            {
                enterChildren = true;
                if (it.propertyType != SerializedPropertyType.ObjectReference || it.name == "m_Script")
                    continue;
                if (it.objectReferenceValue != null)
                    continue;

                var field = FieldFor(type, it.propertyPath);
                if (field != null && field.IsDefined(typeof(OptionalReferenceAttribute), true))
                    continue;

                bool broken = it.objectReferenceInstanceIDValue != 0;
                yield return "ARCH072 " + context + " " + type.Name + "." + it.propertyPath + ": " +
                             (broken ? "reference is broken (target deleted/moved without .meta)" : "required reference is empty") +
                             " (mark the field [OptionalReference] only if empty is valid)";
            }
        }

        private static FieldInfo FieldFor(Type type, string propertyPath)
        {
            string name = propertyPath.Split('.')[0];
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(ScriptableObject); t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null)
                    return f;
            }
            return null;
        }

        private static string PathOf(Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null; p = p.parent)
                path = p.name + "/" + path;
            return path;
        }
    }
}
