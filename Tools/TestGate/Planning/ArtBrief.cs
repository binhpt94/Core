using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TestGate.Planning
{
    /// <summary>
    /// The art "ticket", GENERATED from the contract + the style lock: a brief for the human (Markdown, Vietnamese) and a
    /// prompt for an image model (English). Nobody writes tickets: change the contract or the style file and regenerate.
    /// Pure: no IO; the CLI decides where the text goes.
    /// </summary>
    public static class ArtBrief
    {
        /// <summary>Deliver raster art at this multiple of the contract units (UiImage: 1 canvas unit ≈ 1 px at 1080×1920).</summary>
        public const int DeliveryScale = 2;

        public static string Markdown(ArtSlotSpec s, string featureStyle, string slotStyle, string level, IReadOnlyList<string> shots)
        {
            var rejected = ArtStyle.Rejections(slotStyle);
            string slotOnly = ArtStyle.WithoutRejections(slotStyle);
            var sb = new StringBuilder();
            sb.Append("# Brief art: ").Append(s.Id).Append("\n\n");
            sb.Append("> Sinh tự động từ contract (`").Append(s.Feature).Append("ArtSlots.cs`) và style lock. Đừng sửa file này: sửa contract hoặc `*.style.md`, rồi chạy `./Tools/test-gate.sh art brief ").Append(s.Id).Append("`.\n\n");
            sb.Append("**Mục đích:** ").Append(s.Intent.Length > 0 ? s.Intent : "_(contract chưa có `intent`)_").Append("\n\n");
            sb.Append("**Mức hiện tại:** ").Append(level ?? ArtLevels.Graybox).Append(" · loại `").Append(s.Kind).Append("`\n\n");

            sb.Append("## Khung\n\n");
            sb.Append("| | Tối thiểu | Tối đa | Nộp file (").Append(DeliveryScale).Append("×) |\n|---|---|---|---|\n");
            sb.Append("| Rộng | ").Append(s.MinWidth).Append(" | ").Append(s.MaxWidth).Append(" | ≤ ").Append(s.MaxWidth * DeliveryScale).Append(" px |\n");
            sb.Append("| Cao | ").Append(s.MinHeight).Append(" | ").Append(s.MaxHeight).Append(" | ≤ ").Append(s.MaxHeight * DeliveryScale).Append(" px |\n\n");
            sb.Append("Đơn vị: ").Append(Units(s.Kind)).Append(". Gợi ý vẽ quanh **").Append(Mid(s.MinWidth, s.MaxWidth) * DeliveryScale).Append("×")
              .Append(Mid(s.MinHeight, s.MaxHeight) * DeliveryScale).Append(" px**, nền trong suốt, sát nội dung (không chừa viền trống).\n\n");
            sb.Append("**Pivot:** ").Append(s.PivotXPercent).Append("% ngang, ").Append(s.PivotYPercent).Append("% dọc (±").Append(s.PivotTolerancePercent).Append("%), tính từ góc dưới trái.\n\n");
            sb.Append("```\n").Append(PivotDiagram(s.PivotXPercent, s.PivotYPercent)).Append("```\n\n");

            if (s.Actions.Count > 0)
            {
                sb.Append("## Animation\n\n| Clip | Marker | Bắt buộc | Marker rơi vào | Tốc độ cho phép | Frame gợi ý | File |\n|---|---|---|---|---|---|---|\n");
                foreach (var a in s.Actions)
                    sb.Append("| ").Append(a.Clip).Append(" | `").Append(a.Marker).Append("` | ").Append(a.Required ? "có" : "không (ảnh tĩnh cũng được)")
                      .Append(" | ").Append(Ms(a.MinTick, s.TickRate)).Append("–").Append(Ms(a.MaxTick, s.TickRate)).Append(" ms | ")
                      .Append(a.MinSpeedPercent).Append("–").Append(a.MaxSpeedPercent).Append("% | ").Append(a.Frames > 0 ? a.Frames.ToString() : "-")
                      .Append(" | `").Append(StripFile(s, a)).Append("` |\n");
                sb.Append("\nGameplay giữ timing; art chỉ đặt marker (animation event) vào trong khoảng trên.\n\n");
            }

            sb.Append("## File nộp\n\n- `").Append(s.Name).Append(".png`: ảnh tĩnh\n");
            foreach (var a in s.Actions)
                sb.Append("- `").Append(StripFile(s, a)).Append("`: strip ngang").Append(a.Frames > 0 ? " " + a.Frames + " frame" : "").Append(a.Required ? "" : " (tuỳ chọn)").Append('\n');
            sb.Append('\n');

            sb.Append("## Style lock\n\n");
            sb.Append(featureStyle.Length > 0 ? featureStyle + "\n\n" : "_Chưa có `" + ArtStyle.FeaturePath(s.Feature) + "`._\n\n");
            if (slotOnly.Length > 0)
                sb.Append(slotOnly).Append("\n\n");
            if (rejected.Count > 0)
            {
                sb.Append("**Đã từ chối (đừng lặp lại):**\n\n");
                foreach (var r in rejected)
                    sb.Append("- ").Append(r).Append('\n');
                sb.Append('\n');
            }

            sb.Append("## Ảnh trong game\n\n");
            if (shots == null || shots.Count == 0)
                sb.Append("_Chưa có ảnh: chạy `./Tools/test-gate.sh art shots` (Editor đóng) hoặc *Tools > Game > Art > Snapshot* (Editor mở)._\n");
            else
                foreach (var shot in shots)
                    sb.Append("![").Append(System.IO.Path.GetFileNameWithoutExtension(shot)).Append("](").Append(shot).Append(")\n");
            return sb.ToString();
        }

        public static string Prompt(ArtSlotSpec s, string featureStyle, string slotStyle)
        {
            var rejected = ArtStyle.Rejections(slotStyle);
            string slotOnly = ArtStyle.WithoutRejections(slotStyle);
            var sb = new StringBuilder();
            sb.Append("Game art asset \"").Append(s.Id).Append("\" for a mobile game (portrait 1080x1920).\n");
            if (s.Intent.Length > 0)
                sb.Append("Purpose: ").Append(s.Intent).Append('\n');
            sb.Append("Output: one PNG, transparent background, a single subject, no text, no frame, no drop shadow on the canvas edge, cropped tight to the subject.\n");
            sb.Append("Size: between ").Append(s.MinWidth * DeliveryScale).Append("x").Append(s.MinHeight * DeliveryScale).Append(" and ")
              .Append(s.MaxWidth * DeliveryScale).Append("x").Append(s.MaxHeight * DeliveryScale).Append(" px; aim for about ")
              .Append(Mid(s.MinWidth, s.MaxWidth) * DeliveryScale).Append("x").Append(Mid(s.MinHeight, s.MaxHeight) * DeliveryScale).Append(" px")
              .Append(" (width/height ≈ ").Append(Ratio(Mid(s.MinWidth, s.MaxWidth), Mid(s.MinHeight, s.MaxHeight))).Append(").\n");
            sb.Append("Anchor: the subject's anchor point sits at ").Append(s.PivotXPercent).Append("% from the left and ").Append(s.PivotYPercent)
              .Append("% from the bottom of the image").Append(PivotHint(s.PivotXPercent, s.PivotYPercent)).Append(".\n");
            sb.Append("Readability: it is shown at only ~").Append(Mid(s.MinHeight, s.MaxHeight)).Append(" px tall on a phone, so use a bold silhouette, clear shapes and few small details.\n");
            foreach (var a in s.Actions.Where(a => a.Frames > 0))
                sb.Append("Animation (optional, separate request): '").Append(a.Clip).Append("' as a horizontal strip of ").Append(a.Frames)
                  .Append(" equal frames; the '").Append(a.Marker).Append("' pose happens around frame ")
                  .Append(MarkerFrame(a, s.TickRate)).Append(".\n");
            if (featureStyle.Length > 0)
                sb.Append("\nStyle (feature):\n").Append(featureStyle).Append('\n');
            if (slotOnly.Length > 0)
                sb.Append("\nStyle (this asset):\n").Append(slotOnly).Append('\n');
            if (rejected.Count > 0)
            {
                sb.Append("\nAvoid (earlier versions were rejected for this):\n");
                foreach (var r in rejected)
                    sb.Append("- ").Append(r).Append('\n');
            }
            return sb.ToString();
        }

        public static string StripFile(ArtSlotSpec s, ArtActionSpec a) => s.Name + "_" + a.Clip + ".png";

        /// <summary>5×9 grid with '+' at the pivot (row 0 = top).</summary>
        public static string PivotDiagram(int xPercent, int yPercent)
        {
            const int w = 9, h = 5;
            int col = Clamp((xPercent * (w - 1) + 50) / 100, 0, w - 1);
            int row = Clamp(h - 1 - (yPercent * (h - 1) + 50) / 100, 0, h - 1);
            var sb = new StringBuilder();
            sb.Append('┌').Append(new string('─', w)).Append("┐\n");
            for (int r = 0; r < h; r++)
            {
                sb.Append('│');
                for (int c = 0; c < w; c++)
                    sb.Append(r == row && c == col ? '+' : ' ');
                sb.Append("│\n");
            }
            sb.Append('└').Append(new string('─', w)).Append("┘\n");
            return sb.ToString();
        }

        private static int MarkerFrame(ArtActionSpec a, int tickRate)
        {
            // Marker mid-window at 1x, over a clip that ends ~100 ms after the window (same shape as the graybox).
            int mid = (a.MinTick + a.MaxTick) * 1000 / (2 * tickRate), length = a.MaxTick * 1000 / tickRate + 100;
            return Clamp(1 + mid * a.Frames / Math.Max(1, length), 1, a.Frames);
        }

        private static string PivotHint(int x, int y) =>
            x == 50 && y == 50 ? " (the centre)" : x == 50 && y == 0 ? " (bottom centre: the feet / ground contact)" : "";

        private static string Units(string kind) =>
            kind == "UiImage" ? "canvas unit (≈ 1 px trên màn 1080×1920)" : "1/100 world unit";

        private static string Ms(int tick, int tickRate) => (tick * 1000 / Math.Max(1, tickRate)).ToString();
        private static int Mid(int a, int b) => (a + b) / 2;
        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        private static string Ratio(int w, int h) =>
            (w * 100 / Math.Max(1, h) / 100) + "." + (w * 100 / Math.Max(1, h) % 100).ToString("00");
    }
}
