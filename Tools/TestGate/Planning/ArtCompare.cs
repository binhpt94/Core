using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace TestGate.Planning
{
    /// <summary>
    /// The page the owner judges art on, BY EYE: the slot alone at every level it has (graybox | candidate | final), each with
    /// the contract drawn over it (max envelope, min envelope, pivot), then the feature's views on every device. One note box
    /// per picture turns a one-line reaction into the exact `art note` command (copy, paste in a terminal). Static HTML next to
    /// the brief (Dashboard/Data/art/briefs/&lt;slot&gt;.compare.html), pictures from ../shots/. Pure: no IO.
    /// </summary>
    public static class ArtCompare
    {
        /// <summary>Must match ArtSnapshot (Core/Editor): the slot picture is the max envelope × 5/2, the art's pivot at its centre
        /// (room for the art at any pivot).</summary>
        public const int MarginNumerator = 5, MarginDenominator = 2;

        public static readonly string[] Levels = { "graybox", "candidate", "final" };

        /// <param name="where">in-game shot → its sidecar text from ArtSnapshot ("view V fixture F canvas W H" + "rect x y w h" lines).</param>
        public static string Html(ArtSlotSpec s, string level, IReadOnlyList<string> shots, IReadOnlyList<string> rejections,
            IReadOnlyDictionary<string, string> where = null, IReadOnlyList<VersionCard> versions = null)
        {
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>").Append(E(s.Id)).Append(" · so sánh art</title><style>").Append(Css).Append("</style></head><body>");
            sb.Append("<header><h1>").Append(E(s.Id)).Append("</h1><p class=\"intent\">").Append(E(s.Intent.Length > 0 ? s.Intent : "(contract chưa có intent)"))
              .Append("</p><p class=\"meta\">Mức hiện tại: <b>").Append(E(level ?? ArtLevels.Graybox)).Append("</b> · khung ")
              .Append(s.MinWidth).Append("–").Append(s.MaxWidth).Append(" × ").Append(s.MinHeight).Append("–").Append(s.MaxHeight)
              .Append(" · pivot ").Append(s.PivotXPercent).Append("%,").Append(s.PivotYPercent).Append("% ±").Append(s.PivotTolerancePercent).Append("%</p>");
            sb.Append("<p class=\"legend\"><span class=\"k max\"></span>khung tối đa <span class=\"k min\"></span>khung tối thiểu <span class=\"k piv\"></span>pivot</p></header>");

            sb.Append(VersionGallery(s, versions ?? new VersionCard[0]));
            sb.Append("<h2>Đang gắn (ảnh từ Unity)</h2><section class=\"row\">");
            int levelShots = 0;
            foreach (var l in Levels)
            {
                string file = shots.FirstOrDefault(f => f.EndsWith("/" + s.Id + "." + l + ".png", StringComparison.Ordinal));
                if (file == null)
                    continue;
                levelShots++;
                sb.Append("<figure class=\"slot\"><div class=\"stage\"><img src=\"").Append(E(file)).Append("\" alt=\"").Append(l).Append("\">")
                  .Append(Overlay(s)).Append("</div><figcaption>").Append(l).Append("</figcaption>").Append(NoteBox(s.Id, l)).Append("</figure>");
            }
            if (levelShots == 0)
                sb.Append("<p class=\"empty\">Chưa có ảnh: chạy ./Tools/test-gate.sh art shots (Editor đóng) hoặc Tools &gt; Game &gt; Art &gt; Snapshot (Editor mở), rồi art compare.</p>");
            sb.Append("</section>");

            var views = shots.Where(f => f.Contains("/" + s.Id + ".inview.")).ToList();
            var first = views.Count > 0 && where != null && where.TryGetValue(views[0], out var t0) ? Sidecar.Parse(t0) : null;
            sb.Append("<h2>Trong game").Append(first != null ? " · " + E(first.View) + ", fixture " + E(first.Fixture) : "").Append("</h2>");
            if (views.Count == 0)
                sb.Append("<p class=\"empty\">Slot này chưa xuất hiện trong view nào: chưa có view nào khai báo nó trong <code>ArtPlacements</code> ")
                  .Append("(file <code>Editor/Views/&lt;View&gt;Cases.cs</code> của feature: fixture nào, tìm object ra sao). Khai báo xong, chạy lại Snapshot.</p>");
            else
            {
                sb.Append("<section class=\"row\">");
                foreach (var v in views)
                {
                    string device = DeviceOf(v);
                    var side = where != null && where.TryGetValue(v, out var text) ? Sidecar.Parse(text) : null;
                    sb.Append("<figure class=\"view\">").Append(side != null ? Zoom(v, side) : "")
                      .Append("<a class=\"shot\" href=\"").Append(E(v)).Append("\" target=\"_blank\" title=\"Mở ảnh gốc\"><img src=\"").Append(E(v)).Append("\" alt=\"").Append(E(device)).Append("\">")
                      .Append(side != null ? Here(side) : "").Append("</a><figcaption>").Append(E(device))
                      .Append("</figcaption>").Append(NoteBox(s.Id, device)).Append("</figure>");
                }
                sb.Append("</section>");
            }

            if (rejections.Count > 0)
            {
                sb.Append("<h2>Đã từ chối trước đây</h2><ul class=\"rejected\">");
                foreach (var r in rejections)
                    sb.Append("<li>").Append(E(r)).Append("</li>");
                sb.Append("</ul>");
            }
            sb.Append("<script>").Append(Script).Append("</script></body></html>");
            return sb.ToString();
        }

        /// <summary>
        /// Every version of the slot, newest first, straight from its PNG (no Unity needed): placed so its pivot sits at the frame
        /// centre, the contract drawn over it, with tier / author / date / note, what each tier uses, what a human approved, a note
        /// box (→ `art note`) and the `art use` command to switch or roll back.
        /// </summary>
        public static string VersionGallery(ArtSlotSpec s, IReadOnlyList<VersionCard> versions)
        {
            var sb = new StringBuilder("<h2>Các version</h2>");
            if (versions.Count == 0)
                return sb.Append("<p class=\"empty\">Chưa có version nào (đang là graybox). Claude vẽ bản đầu: <code>./Tools/test-gate.sh art add ")
                    .Append(E(s.Id)).Append(" draft.svg --tier candidate --by claude</code>.</p>").ToString();
            sb.Append("<section class=\"row\">");
            foreach (var c in versions.OrderByDescending(c => c.Version.Number))
            {
                var v = c.Version;
                sb.Append("<figure class=\"slot").Append(c.Uses.Count > 0 ? " inuse" : "").Append("\"><div class=\"stage\">").Append(Placed(s, c)).Append("</div>");
                sb.Append("<figcaption>").Append(E(v.Id)).Append(" · ").Append(E(v.Tier)).Append(" · ").Append(E(v.By));
                foreach (var u in c.Uses)
                    sb.Append(" <span class=\"badge use\">đang dùng: ").Append(E(u)).Append("</span>");
                if (c.Approved)
                    sb.Append(" <span class=\"badge ok\">đã duyệt</span>");
                sb.Append("</figcaption><p class=\"vmeta\">").Append(E(v.Date)).Append(v.Parent != "-" ? " · từ " + E(v.Parent) : "")
                  .Append(" · ").Append(c.Width / ArtBrief.DeliveryScale).Append("×").Append(c.Height / ArtBrief.DeliveryScale)
                  .Append(v.Note.Length > 0 ? "<br>" + E(v.Note) : "").Append("</p>").Append(NoteBox(s.Id, v.Id))
                  .Append("<div class=\"note\"><button type=\"button\" data-copy=\"./Tools/test-gate.sh art use ").Append(E(s.Id)).Append(' ').Append(E(v.Id))
                  .Append("\">Copy lệnh dùng ").Append(E(v.Id)).Append("</button></div></figure>");
            }
            return sb.Append("</section>").ToString();
        }

        /// <summary>The version's PNG in the slot frame: its pivot (the contract's) at the centre, then the contract over it.</summary>
        public static string Placed(ArtSlotSpec s, VersionCard c)
        {
            int w = s.MaxWidth * MarginNumerator / MarginDenominator, h = s.MaxHeight * MarginNumerator / MarginDenominator;
            double iw = (double)c.Width / ArtBrief.DeliveryScale, ih = (double)c.Height / ArtBrief.DeliveryScale;
            double x = w / 2.0 - iw * s.PivotXPercent / 100.0, y = h / 2.0 - ih * (100 - s.PivotYPercent) / 100.0;
            string overlay = Overlay(s).Replace("class=\"overlay\"", "class=\"overlay placed\"");
            int open = overlay.IndexOf('>') + 1;
            return overlay.Substring(0, open) + "<image href=\"" + E(c.Src) + "\" x=\"" + N(x) + "\" y=\"" + N(y) + "\" width=\"" + N(iw) + "\" height=\"" + N(ih) +
                   "\" preserveAspectRatio=\"none\"/>" + overlay.Substring(open);
        }

        /// <summary>The contract over the slot picture, in canvas units (viewBox = picture size in units, y down).</summary>
        public static string Overlay(ArtSlotSpec s)
        {
            int w = s.MaxWidth * MarginNumerator / MarginDenominator, h = s.MaxHeight * MarginNumerator / MarginDenominator;
            double cx = w / 2.0, cy = h / 2.0;
            var sb = new StringBuilder();
            sb.Append("<svg class=\"overlay\" viewBox=\"0 0 ").Append(w).Append(' ').Append(h).Append("\" preserveAspectRatio=\"none\">");
            sb.Append(Box(s.MaxWidth, s.MaxHeight, s, cx, cy, "max"));
            sb.Append(Box(s.MinWidth, s.MinHeight, s, cx, cy, "min"));
            double arm = Math.Max(3, Math.Min(w, h) / 12.0);
            string cross = "M" + N(cx - arm) + " " + N(cy) + "H" + N(cx + arm) + "M" + N(cx) + " " + N(cy - arm) + "V" + N(cy + arm);
            sb.Append("<path class=\"halo\" d=\"").Append(cross).Append("\"/>");
            sb.Append("<path class=\"piv\" d=\"M").Append(N(cx - arm)).Append(' ').Append(N(cy)).Append('H').Append(N(cx + arm))
              .Append("M").Append(N(cx)).Append(' ').Append(N(cy - arm)).Append('V').Append(N(cy + arm)).Append("\"/></svg>");
            return sb.ToString();
        }

        /// <summary>
        /// Close-up of the first instance in the game: the same picture cropped by the SVG viewBox to the slot plus one slot-size
        /// of context on each side (canvas units), so the art is judged at a readable size against what surrounds it.
        /// </summary>
        public static string Zoom(string shot, Sidecar side)
        {
            if (side.Rects.Count == 0)
                return "";
            var r = side.Rects[0];
            int pad = Math.Max(r[2], r[3]);
            int x = r[0] - pad, y = r[1] - pad, w = r[2] + 2 * pad, h = r[3] + 2 * pad;
            string box = " x=\"" + (r[0] - 4) + "\" y=\"" + (r[1] - 4) + "\" width=\"" + (r[2] + 8) + "\" height=\"" + (r[3] + 8) + "\"/>";
            return "<svg class=\"zoom\" viewBox=\"" + x + " " + y + " " + w + " " + h + "\"><image href=\"" + E(shot) + "\" x=\"0\" y=\"0\" width=\"" + side.Width +
                   "\" height=\"" + side.Height + "\" preserveAspectRatio=\"none\"/><rect class=\"halo\"" + box + "<rect class=\"here\"" + box + "</svg>";
        }

        /// <summary>Where the slot sits in an in-game shot (one box per instance, canvas units).</summary>
        public static string Here(Sidecar side)
        {
            var sb = new StringBuilder("<svg class=\"overlay\" viewBox=\"0 0 " + side.Width + " " + side.Height + "\" preserveAspectRatio=\"none\">");
            foreach (var r in side.Rects)
            {
                string attrs = " x=\"" + (r[0] - 6) + "\" y=\"" + (r[1] - 6) + "\" width=\"" + (r[2] + 12) + "\" height=\"" + (r[3] + 12) + "\"/>";
                sb.Append("<rect class=\"halo\"").Append(attrs).Append("<rect class=\"here\"").Append(attrs);
            }
            return sb.Append("</svg>").ToString();
        }

        /// <summary>A box of the given size placed so that its pivot point sits at the picture centre.</summary>
        private static string Box(int bw, int bh, ArtSlotSpec s, double cx, double cy, string cls)
        {
            double left = cx - bw * s.PivotXPercent / 100.0, top = cy - bh * (100 - s.PivotYPercent) / 100.0;
            string attrs = " x=\"" + N(left) + "\" y=\"" + N(top) + "\" width=\"" + bw + "\" height=\"" + bh + "\"/>";
            return "<rect class=\"halo\"" + attrs + "<rect class=\"" + cls + "\"" + attrs; // white halo: readable on any art colour
        }

        private static string NoteBox(string id, string where) =>
            "<div class=\"note\"><input type=\"text\" placeholder=\"Góp ý 1 dòng\" data-slot=\"" + E(id) + "\" data-where=\"" + E(where) +
            "\"><button type=\"button\">Copy lệnh</button><code></code></div>";

        public static string DeviceOf(string viewShot)
        {
            string name = viewShot.Substring(viewShot.LastIndexOf('/') + 1);
            name = name.EndsWith(".png", StringComparison.Ordinal) ? name.Substring(0, name.Length - 4) : name;
            int dot = name.LastIndexOf('.');
            return dot < 0 ? name : name.Substring(dot + 1);
        }

        private static string N(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        private static string E(string s) => WebUtility.HtmlEncode(s ?? "");

        private const string Css =
            ":root{--bg:#f6f6f4;--fg:#1d1d1f;--muted:#6b6b70;--card:#fff;--line:#d9d9de;--max:#e5484d;--min:#3e8ed0;--piv:#12a150}" +
            "@media (prefers-color-scheme:dark){:root{--bg:#16161a;--fg:#ececf0;--muted:#9a9aa3;--card:#202026;--line:#34343c}}" +
            "*{box-sizing:border-box}body{margin:0;padding:16px;background:var(--bg);color:var(--fg);font:15px/1.45 system-ui,sans-serif}" +
            "h1{margin:0 0 4px;font-size:22px}h2{font-size:16px;margin:28px 0 10px}.intent{margin:0 0 6px}.meta,.legend{margin:0;color:var(--muted);font-size:13px}" +
            ".k{display:inline-block;width:14px;height:10px;margin:0 4px 0 10px;border:2px solid}.k.max{border-color:var(--max)}.k.min{border-color:var(--min);border-style:dashed}.k.piv{border-color:var(--piv);border-radius:50%}" +
            ".row{display:flex;flex-wrap:wrap;gap:16px;align-items:flex-start}figure{margin:0;background:var(--card);border:1px solid var(--line);border-radius:10px;padding:10px;max-width:100%}" +
            "figcaption{font-weight:600;margin:8px 0 6px}.stage{position:relative;width:240px;max-width:100%;background:repeating-conic-gradient(#ccc 0 25%,#fff 0 50%) 0 0/16px 16px}" +
            ".stage img{display:block;width:100%;height:auto;image-rendering:pixelated}.overlay{position:absolute;inset:0;width:100%;height:100%}" +
            ".overlay rect,.overlay path{fill:none;vector-effect:non-scaling-stroke}.overlay rect{stroke-width:1.5}.overlay .halo{stroke:#fff;stroke-width:4;stroke-opacity:.85;stroke-dasharray:none}" +
            ".overlay .here,.zoom .here{fill:none;stroke:var(--max);stroke-width:2.5;vector-effect:non-scaling-stroke}.shot{position:relative;display:block}" +
            ".zoom{display:block;width:100%;aspect-ratio:1/1;margin-bottom:8px;border-radius:6px;background:#1e1e24}.zoom .halo{fill:none;stroke:#fff;stroke-width:4;stroke-opacity:.85;vector-effect:non-scaling-stroke}.overlay .max{stroke:var(--max)}.overlay .min{stroke:var(--min);stroke-width:2;stroke-dasharray:4 3}" +
            ".overlay .piv{stroke:var(--piv);stroke-width:2;vector-effect:non-scaling-stroke}figure.view{width:250px}.view img{display:block;width:100%;height:auto;border-radius:6px}" +
            ".note{display:flex;flex-wrap:wrap;gap:6px;width:100%;max-width:250px}figure.slot{width:262px}.note input{flex:1;min-width:0;padding:6px 8px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg)}" +
            ".note button{padding:6px 10px;border:1px solid var(--line);border-radius:6px;background:var(--card);color:var(--fg);cursor:pointer}" +
            ".overlay.placed{position:static;display:block;height:auto}.badge{font-size:11px;font-weight:600;padding:1px 6px;border-radius:9px;margin-left:4px}" +
            ".badge.use{background:#dbeafe;color:#1e40af}.badge.ok{background:#dcfce7;color:#166534}figure.inuse{border-color:#3e8ed0}.vmeta{margin:0 0 6px;font-size:12px;color:var(--muted)}" +
            ".note code{display:block;width:100%;font-size:11px;color:var(--muted);word-break:break-all}.empty{color:var(--muted)}.rejected{padding-left:20px}";

        private const string Script =
            "document.querySelectorAll('.note').forEach(function(n){var i=n.querySelector('input'),b=n.querySelector('button'),c=n.querySelector('code');if(!i)return;" +
            "function cmd(){var t=i.value.trim().replace(/\"/g,\"'\").replace(/[$`\\\\]/g,'');return t?'./Tools/test-gate.sh art note '+i.dataset.slot+' \"'+i.dataset.where+': '+t+'\"':'';}" +
            "i.addEventListener('input',function(){c.textContent=cmd();});" +
            "b.addEventListener('click',function(){var v=cmd();if(!v)return;c.textContent=v;try{navigator.clipboard.writeText(v);b.textContent='Đã copy';setTimeout(function(){b.textContent='Copy lệnh';},1500);}catch(e){}});});" +
            "document.querySelectorAll('button[data-copy]').forEach(function(b){var t=b.textContent;b.addEventListener('click',function(){try{navigator.clipboard.writeText(b.dataset.copy);b.textContent='Đã copy';setTimeout(function(){b.textContent=t;},1500);}catch(e){}});});";
    }

    /// <summary>One version for the gallery: its PNG (path relative to the page) and pixel size, what uses it, whether approved.</summary>
    public sealed class VersionCard
    {
        public ArtVersion Version { get; set; }
        public string Src { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public List<string> Uses { get; } = new List<string>();
        public bool Approved { get; set; }

        /// <summary>Width/height from a PNG's IHDR (bytes 16..23); (0,0) when not a PNG.</summary>
        public static (int w, int h) PngSize(byte[] b)
        {
            if (b == null || b.Length < 24 || b[0] != 0x89 || b[1] != (byte)'P')
                return (0, 0);
            return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
        }
    }

    /// <summary>ArtSnapshot's sidecar of an in-game shot: which view/fixture, the canvas size, the slot's rects (canvas units, y down).</summary>
    public sealed class Sidecar
    {
        public string View { get; private set; } = "";
        public string Fixture { get; private set; } = "";
        public int Width { get; private set; }
        public int Height { get; private set; }
        public List<int[]> Rects { get; } = new List<int[]>();

        public static Sidecar Parse(string text)
        {
            var side = new Sidecar();
            foreach (var line in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var f = line.Split(' ');
                if (f[0] == "view" && f.Length >= 7)
                {
                    side.View = f[1];
                    side.Fixture = f[3];
                    side.Width = Int(f[5]);
                    side.Height = Int(f[6]);
                }
                else if (f[0] == "rect" && f.Length >= 5)
                    side.Rects.Add(new[] { Int(f[1]), Int(f[2]), Int(f[3]), Int(f[4]) });
            }
            return side.Width > 0 && side.Height > 0 ? side : null;
        }

        private static int Int(string s) => int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;
    }
}
