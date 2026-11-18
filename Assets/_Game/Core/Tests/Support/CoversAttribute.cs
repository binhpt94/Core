using System;
using NUnit.Framework;

namespace Game.TestSupport
{
    /// <summary>
    /// Links a test (method or whole class) to a requirement of plan/features.md. The requirement's status is COMPUTED from
    /// these tests' results (nobody sets "verified" by hand). Unknown ids fail ARCH111. Shows in NUnit as property "Covers".
    ///   [Test, Covers("R-TIMER-DEADLINE")] public void TimesOut_ExactlyAtTheDeadlineTick() ...
    /// </summary>
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
    public sealed class CoversAttribute : PropertyAttribute
    {
        public string RequirementId { get; }

        public CoversAttribute(string requirementId) : base("Covers", requirementId) => RequirementId = requirementId;
    }
}
