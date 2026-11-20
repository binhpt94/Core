using System;
using System.IO;
using NUnit.Framework;
using TestGate.Planning;

namespace LogicTests.AgentControl
{
    /// <summary>The art ticket is generated: contract + style lock → brief (human) and prompt (image model).</summary>
    public sealed class ArtBriefTests
    {
        private static ArtSlotSpec Slime()
        {
            var s = new ArtSlotSpec
            {
                Id = "MathQuiz.Slime", Kind = "UiImage", MinWidth = 32, MaxWidth = 72, MinHeight = 60, MaxHeight = 110,
                PivotXPercent = 50, PivotYPercent = 50, PivotTolerancePercent = 10, TickRate = 20, Intent = "Enemy walking along a lane"
            };
            s.Actions.Add(new ArtActionSpec { Clip = "Hop", Marker = "land", MinTick = 2, MaxTick = 6, MinSpeedPercent = 80, MaxSpeedPercent = 125, Required = false, Frames = 6 });
            return s;
        }

        private const string SlotStyle = "# Slime\n\nSoft jelly, glossy highlight.\n\n" + ArtStyle.RejectedHeading + "\n- 2026-11-20: too dark on the lane\n- 2026-11-20: eyes too small\n";

        [Test]
        public void SpecFormat_RoundTrips_AndFlattensTabsInIntent()
        {
            var s = Slime();
            s.Intent = "a\tb\nc";
            var back = ArtSlotSpecs.Parse(ArtSlotSpecs.Format(new[] { s }));
            Assert.That(back.Count, Is.EqualTo(1));
            Assert.That(back[0].Intent, Is.EqualTo("a b c"));
            Assert.That(back[0].Actions[0].Frames, Is.EqualTo(6));
            Assert.That(back[0].Actions[0].Required, Is.False);
            Assert.That(back[0].Feature, Is.EqualTo("MathQuiz"));
            Assert.That(back[0].Name, Is.EqualTo("Slime"));
        }

        [Test]
        public void Brief_CarriesTheContractNumbers_TheStyleLock_AndTheRejections()
        {
            string md = ArtBrief.Markdown(Slime(), "Palette: pastel.", SlotStyle, "GRAYBOX", new[] { "../shots/MathQuiz.Slime.graybox.phone-16x9.png" });
            Assert.That(md, Does.Contain("Enemy walking along a lane"));
            Assert.That(md, Does.Contain("| Rộng | 32 | 72 | ≤ 144 px |"));
            Assert.That(md, Does.Contain("| Hop | `land` | không (ảnh tĩnh cũng được) | 100–300 ms | 80–125% | 6 | `Slime_Hop.png` |"));
            Assert.That(md, Does.Contain("Palette: pastel."));
            Assert.That(md, Does.Contain("Soft jelly"));
            Assert.That(md, Does.Contain("- 2026-11-20: eyes too small"));
            Assert.That(md, Does.Contain("](../shots/MathQuiz.Slime.graybox.phone-16x9.png)"));
            Assert.That(md, Does.Not.Contain(ArtStyle.RejectedHeading), "rejections are rendered once, in their own list");
        }

        [Test]
        public void Prompt_HasSizeAnchorStyleAndAvoidList()
        {
            string p = ArtBrief.Prompt(Slime(), "Palette: pastel.", SlotStyle);
            Assert.That(p, Does.Contain("transparent background"));
            Assert.That(p, Does.Contain("between 64x120 and 144x220 px; aim for about 104x170 px"));
            Assert.That(p, Does.Contain("50% from the left and 50% from the bottom of the image (the centre)"));
            Assert.That(p, Does.Contain("horizontal strip of 6 equal frames"));
            Assert.That(p, Does.Contain("Avoid (earlier versions were rejected for this):\n- 2026-11-20: too dark on the lane\n- 2026-11-20: eyes too small"));
        }

        [Test]
        public void StyleClean_DropsTitleAndOwnerComments_KeepsSections()
        {
            string cleaned = ArtStyle.Clean("# MathQuiz style\n<!-- note to self -->\n- bold outline\n## Palette\n- pastel\n");
            Assert.That(cleaned, Is.EqualTo("- bold outline\n## Palette\n- pastel"));
        }

        [Test]
        public void PivotDiagram_PutsThePlusWhereThePivotIs()
        {
            Assert.That(ArtBrief.PivotDiagram(50, 50), Does.Contain("│    +    │"));
            var bottom = ArtBrief.PivotDiagram(50, 0).Split('\n');
            Assert.That(bottom[5], Is.EqualTo("│    +    │"), "pivot at the bottom row");
        }

        [Test]
        public void Note_AppendsUnderTheRejectedHeading_CreatingTheFileOnce()
        {
            string root = Path.Combine(Path.GetTempPath(), "artnote-" + Guid.NewGuid().ToString("N"));
            try
            {
                string rel = ArtStyle.AppendRejection(root, Slime(), "too dark", "2026-11-20");
                ArtStyle.AppendRejection(root, Slime(), "eyes\ntoo small", "2026-11-21");
                Assert.That(rel, Is.EqualTo("UnityProject/Assets/_Game/Features/MathQuiz/Assets/Private/Art/Slime/Slime.style.md"));
                string text = File.ReadAllText(Path.Combine(root, rel));
                Assert.That(ArtStyle.Rejections(text), Is.EqualTo(new[] { "2026-11-20: too dark", "2026-11-21: eyes too small" }));

                // Notes the owner wrote below the section stay below it.
                File.WriteAllText(Path.Combine(root, rel), text + "\n## Later\nkeep me\n");
                ArtStyle.AppendRejection(root, Slime(), "third", "2026-11-22");
                text = File.ReadAllText(Path.Combine(root, rel));
                Assert.That(ArtStyle.Rejections(text).Count, Is.EqualTo(3));
                Assert.That(text.IndexOf("third", StringComparison.Ordinal), Is.LessThan(text.IndexOf("## Later", StringComparison.Ordinal)));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        [Test]
        public void Compare_ShowsEachLevelWithTheContractOver_ThenTheViews_AndOnlyPicturesThatExist()
        {
            var shots = new[] { "../shots/MathQuiz.Slime.graybox.png", "../shots/MathQuiz.Slime.candidate.png", "../shots/MathQuiz.Slime.inview.phone-16x9.png" };
            var where = new System.Collections.Generic.Dictionary<string, string>
                { ["../shots/MathQuiz.Slime.inview.phone-16x9.png"] = "view MathQuiz/QuizView fixture smoke canvas 1080 1920\nrect 100 200 52 85\nrect 400 200 52 85\n" };
            string html = ArtCompare.Html(Slime(), "CANDIDATE", shots, new[] { "2026-11-20: too dark" }, where);
            Assert.That(html, Does.Contain("src=\"../shots/MathQuiz.Slime.graybox.png\""));
            Assert.That(html, Does.Contain("src=\"../shots/MathQuiz.Slime.candidate.png\""));
            Assert.That(html, Does.Not.Contain("MathQuiz.Slime.final.png"), "no picture, no column");
            Assert.That(html, Does.Contain("<figcaption>phone-16x9</figcaption>"));
            Assert.That(html, Does.Contain("<li>2026-11-20: too dark</li>"));
            Assert.That(html, Does.Contain("data-slot=\"MathQuiz.Slime\" data-where=\"candidate\""));
            Assert.That(html, Does.Contain("MathQuiz/QuizView, fixture smoke"));
            Assert.That(html, Does.Contain("viewBox=\"0 0 1080 1920\""));
            Assert.That(html, Does.Contain("<rect class=\"here\" x=\"94\" y=\"194\" width=\"64\" height=\"97\"/>"), "box around each instance, 6 units out");
            Assert.That(html, Does.Contain("<rect class=\"here\" x=\"394\""));
            // Close-up of the first slime: 52x85 at (100,200) plus 85 units of context each side, same picture cropped by viewBox.
            Assert.That(html, Does.Contain("<svg class=\"zoom\" viewBox=\"15 115 222 255\"><image href=\"../shots/MathQuiz.Slime.inview.phone-16x9.png\" x=\"0\" y=\"0\" width=\"1080\" height=\"1920\""));
            Assert.That(html, Does.Contain("href=\"../shots/MathQuiz.Slime.inview.phone-16x9.png\" target=\"_blank\""), "click opens the full picture");
        }

        [Test]
        public void Compare_SaysWhenTheSlotIsInNoView_InsteadOfShowingUnrelatedScreens()
        {
            string html = ArtCompare.Html(Slime(), null, new[] { "../shots/MathQuiz.Slime.graybox.png" }, new string[0]);
            Assert.That(html, Does.Contain("chưa xuất hiện trong view nào"));
            Assert.That(Sidecar.Parse("garbage"), Is.Null);
        }

        [Test]
        public void CompareOverlay_PlacesTheEnvelopeSoItsPivotIsThePictureCentre()
        {
            // Picture = max 72x110 × 5/2 = 180x275 units, centre (90, 137.5). Pivot 50/50: max box spans 54..126 × 82.5..192.5.
            string svg = ArtCompare.Overlay(Slime());
            Assert.That(svg, Does.Contain("viewBox=\"0 0 180 275\""));
            Assert.That(svg, Does.Contain("<rect class=\"max\" x=\"54\" y=\"82.5\" width=\"72\" height=\"110\"/>"));
            Assert.That(svg, Does.Contain("<rect class=\"min\" x=\"74\" y=\"107.5\" width=\"32\" height=\"60\"/>"));

            var feet = Slime();
            feet.PivotYPercent = 0; // bottom-centre pivot: the box sits ABOVE the centre, and still inside the picture
            Assert.That(ArtCompare.Overlay(feet), Does.Contain("<rect class=\"max\" x=\"54\" y=\"27.5\" width=\"72\" height=\"110\"/>"));
        }

        [Test]
        public void Compare_EscapesText() =>
            Assert.That(ArtCompare.Html(new ArtSlotSpec { Id = "A.B", Kind = "UiImage", MinWidth = 1, MaxWidth = 2, MinHeight = 1, MaxHeight = 2, Intent = "<b>x</b>" },
                null, new string[0], new string[0]), Does.Contain("&lt;b&gt;x&lt;/b&gt;"));

        [Test]
        public void VersionGallery_NewestFirst_PlacedByPivot_WithUseAndApproval()
        {
            var v1 = new VersionCard { Version = new ArtVersion { Id = "v001", Date = "d", Tier = "candidate", By = "claude", Parent = "-" }, Src = "../versions/x/v001.png", Width = 104, Height = 170, Approved = true };
            var v2 = new VersionCard { Version = new ArtVersion { Id = "v002", Date = "d", Tier = "final", By = "codex", Parent = "v001", Note = "from v001" }, Src = "../versions/x/v002.png", Width = 100, Height = 160 };
            v2.Uses.Add("final");
            string html = ArtCompare.Html(Slime(), null, new string[0], new string[0], null, new[] { v1, v2 });
            Assert.That(html.IndexOf("v002 · final · codex", System.StringComparison.Ordinal), Is.LessThan(html.IndexOf("v001 · candidate · claude", System.StringComparison.Ordinal)));
            Assert.That(html, Does.Contain("đang dùng: final"));
            Assert.That(html, Does.Contain("đã duyệt"));
            Assert.That(html, Does.Contain("data-copy=\"./Tools/test-gate.sh art use MathQuiz.Slime v001\""));
            // v001: 104x170 px = 52x85 units; frame 180x275, centre (90,137.5), pivot 50/50 → image at (64, 95).
            Assert.That(html, Does.Contain("<image href=\"../versions/x/v001.png\" x=\"64\" y=\"95\" width=\"52\" height=\"85\""));
        }

        [Test]
        public void PngSize_ReadsTheHeader() =>
            Assert.That(VersionCard.PngSize(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R', 0, 0, 0, 40, 0, 0, 1, 44 }),
                Is.EqualTo((40, 300)));
    }
}
