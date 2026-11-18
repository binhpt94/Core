using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Game.Architecture.Tests
{
    /// <summary>Finds compiled game types for reflection rules (dotnet: LogicTests.dll; Unity: Game.* assemblies).</summary>
    internal static class GameTypeScanner
    {
        public static IEnumerable<Type> GameTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                bool isGame = name.StartsWith("Game.", StringComparison.Ordinal) || name == "LogicTests";
                if (!isGame || name.EndsWith(".Tests", StringComparison.Ordinal))
                    continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    types = e.Types.Where(t => t != null).ToArray();
                }

                foreach (var type in types)
                {
                    if (type.Namespace == null
                        || !type.Namespace.StartsWith("Game.", StringComparison.Ordinal)
                        || type.Namespace.Contains(".Tests")
                        || type.Namespace.StartsWith("Game.Architecture", StringComparison.Ordinal) // tooling, not game code
                        || IsCompilerGenerated(type))
                        continue;

                    yield return type;
                }
            }
        }

        public static bool IsCompilerGenerated(Type type)
        {
            for (var t = type; t != null; t = t.DeclaringType)
                if (t.IsDefined(typeof(CompilerGeneratedAttribute), false))
                    return true;
            return false;
        }

    }
}
