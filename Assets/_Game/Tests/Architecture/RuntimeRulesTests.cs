using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace Game.Architecture.Tests
{
    /// <summary>
    /// Rules checked by reflection on the compiled game types (more robust than text search):
    /// - no mutable static state, no static events  → replay / Enter-Play-Mode-without-domain-reload bugs
    /// - no async void / UniTaskVoid                 → exceptions lost, work without an owner
    /// In `dotnet test` this scans Core + Contracts + Logic. In Unity EditMode it also scans
    /// the Unity-layer assemblies (Game.*.Unity, Game.Bootstrap) because they are loaded there.
    /// Escape hatch: [Game.Core.AllowStatic("reason")] (matched by name, so no reference is needed).
    /// </summary>
    public sealed class RuntimeRulesTests
    {
        private const BindingFlags StaticMembers =
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private const BindingFlags AllMethods =
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static readonly HashSet<string> MutableGenericCollections = new HashSet<string>
        {
            "System.Collections.Generic.List`1",
            "System.Collections.Generic.Dictionary`2",
            "System.Collections.Generic.HashSet`1",
            "System.Collections.Generic.Queue`1",
            "System.Collections.Generic.Stack`1",
            "System.Collections.Generic.LinkedList`1",
            "System.Collections.Generic.SortedDictionary`2",
            "System.Collections.Generic.SortedList`2",
            "System.Collections.Generic.SortedSet`1",
        };

        [Test]
        public void GameTypes_AreFound()
        {
            // Guards the other tests against silently scanning nothing.
            Assert.That(GameTypeScanner.GameTypes().Any(t => t.FullName == "Game.Core.Lifetime"), Is.True);
        }

        [Test]
        public void NoMutableStaticState()
        {
            var offenders = new List<string>();

            foreach (var type in GameTypeScanner.GameTypes())
            {
                var eventNames = new HashSet<string>(type.GetEvents(StaticMembers).Select(e => e.Name));

                foreach (var field in type.GetFields(StaticMembers))
                {
                    if (field.IsLiteral || IsAllowed(field) || eventNames.Contains(field.Name))
                        continue;

                    string name = DisplayName(type, field);

                    if (!field.IsInitOnly)
                        offenders.Add(name + ": static and writable (make it const/readonly, or move it into a session/lifetime object)");
                    else if (IsMutableCollection(field.FieldType))
                        offenders.Add(name + ": static readonly but the " + field.FieldType.Name + " itself is mutable");
                }
            }

            AssertNone(offenders, "Mutable static state survives replays and Play Mode without domain reload");
        }

        [Test]
        public void NoStaticEvents()
        {
            var offenders = GameTypeScanner.GameTypes()
                .SelectMany(t => t.GetEvents(StaticMembers).Where(e => !IsAllowed(e)).Select(e => t.FullName + "." + e.Name))
                .ToList();

            AssertNone(offenders, "Static events accumulate listeners across replays; use instance events owned by a Lifetime");
        }

        [Test]
        public void NoAsyncVoidOrUniTaskVoid()
        {
            var offenders = new List<string>();

            foreach (var type in GameTypeScanner.GameTypes())
            {
                foreach (var method in type.GetMethods(AllMethods))
                {
                    if (!method.IsDefined(typeof(AsyncStateMachineAttribute), false))
                        continue;

                    if (method.ReturnType == typeof(void))
                        offenders.Add(type.FullName + "." + method.Name + ": async void");
                    else if (method.ReturnType.FullName == "Cysharp.Threading.Tasks.UniTaskVoid")
                        offenders.Add(type.FullName + "." + method.Name + ": async UniTaskVoid");
                }
            }

            AssertNone(offenders, "Return UniTask and start it with Lifetime.Run(...)");
        }

        // ---------- helpers ----------

        private static bool IsMutableCollection(Type type)
        {
            if (type.IsArray || type == typeof(System.Text.StringBuilder) || type == typeof(System.Collections.ArrayList))
                return true;
            return type.IsGenericType && MutableGenericCollections.Contains(type.GetGenericTypeDefinition().FullName);
        }

        private static bool IsAllowed(MemberInfo member)
        {
            var attribute = member.GetCustomAttributes(false)
                .FirstOrDefault(a => a.GetType().FullName == "Game.Core.AllowStaticAttribute");
            if (attribute == null && member is FieldInfo field && field.Name.EndsWith(">k__BackingField", StringComparison.Ordinal))
            {
                // Auto-property backing field: the attribute sits on the property.
                string propertyName = field.Name.Substring(1, field.Name.IndexOf('>') - 1);
                var property = field.DeclaringType.GetProperty(propertyName, StaticMembers);
                if (property != null)
                    attribute = property.GetCustomAttributes(false)
                        .FirstOrDefault(a => a.GetType().FullName == "Game.Core.AllowStaticAttribute");
            }

            if (attribute == null)
                return false;

            var reason = attribute.GetType().GetProperty("Reason")?.GetValue(attribute) as string;
            return !string.IsNullOrWhiteSpace(reason);
        }

        private static string DisplayName(Type type, FieldInfo field)
        {
            string name = field.Name;
            if (name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                name = name.Substring(1, name.IndexOf('>') - 1) + " (auto-property)";
            return type.FullName + "." + name;
        }

        private static void AssertNone(List<string> offenders, string rule) =>
            Assert.That(offenders, Is.Empty, rule + ":\n  " + string.Join("\n  ", offenders));
    }
}
