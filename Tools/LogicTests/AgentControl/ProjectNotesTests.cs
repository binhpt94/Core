using System.Linq;
using NUnit.Framework;
using TestGate.AgentControl;

namespace LogicTests.AgentControl
{
    public sealed class ProjectNotesTests
    {
        [Test]
        public void Notes_AreParsed_NewestFirst()
        {
            var r = ProjectNotes.Parse("# Notes\n\n## N-1  Ý tưởng boss  [date: 2026-11-18]\nCâu 10 có boss.\nHai dòng.\n\n## N-2  Đổi màu slime  [date: 2026-11-19]\nXanh lá.\n");
            Assert.That(r.Errors, Is.Empty);
            Assert.That(r.Notes.Select(n => n.Id), Is.EqualTo(new[] { "N-2", "N-1" }));
            Assert.That(r.Notes[1].Title, Is.EqualTo("Ý tưởng boss"));
            Assert.That(r.Notes[1].Date, Is.EqualTo("2026-11-18"));
            Assert.That(r.Notes[1].Body, Is.EqualTo("Câu 10 có boss.\nHai dòng."));
        }

        [Test]
        public void BadHeadings_AreReported_WithTheLineNumber()
        {
            var r = ProjectNotes.Parse("# Notes\n## N-1  Không có ngày\n## N-2  Ngày sai  [date: hôm qua]\n## N-3  Đúng  [date: 2026-11-19]\n");
            Assert.That(r.Errors, Has.Some.StartsWith("Notes.md:2:"));
            Assert.That(r.Errors, Has.Some.StartsWith("Notes.md:3:"));
            Assert.That(r.Notes.Select(n => n.Id), Is.EqualTo(new[] { "N-3" }));
        }

        [Test]
        public void AnAI_MayFillTheNotes_InAnyTask_TheCoreIdeaGoesThroughTheOwnersYesNo()
        {
            var task = ScopePolicy.Start("T1", "x", new[] { "A" }, new string[0], false, "now");
            Assert.That(ScopePolicy.Check(task, "Docs/Plan/Notes.md").Allowed, Is.True);
            Assert.That(ScopePolicy.Check(task, "Docs/Plan/Core.md").Allowed, Is.False, "Core.md: only Edit/Write after the owner's yes (PlanEditGate)");
            Assert.That(PlanEditGate.IsGated("Docs/Plan/Core.md"), Is.True);
            Assert.That(ScopePolicy.Check(task, "Docs/Plan/features.md").Allowed, Is.False, "the plan stays the owner's");
        }
    }
}
