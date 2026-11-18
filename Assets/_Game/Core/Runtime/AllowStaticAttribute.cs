using System;

namespace Game.Core
{
    /// <summary>
    /// Escape hatch for ArchitectureTests.NoMutableStaticState. Every use needs a real reason and
    /// a reset strategy (e.g. [RuntimeInitializeOnLoadMethod(SubsystemRegistration)]), and should be rare.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Property, Inherited = false)]
    public sealed class AllowStaticAttribute : Attribute
    {
        public string Reason { get; }

        public AllowStaticAttribute(string reason) => Reason = reason;
    }
}
