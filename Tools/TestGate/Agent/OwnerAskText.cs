using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Game.Architecture.Rules;
using TestGate.Planning;

namespace TestGate.AgentControl
{
    /// <summary>
    /// The owner's yes/no dialog in plain Vietnamese, always the same shape: what is asked, the content in words, where it stands,
    /// what Yes does, what No does, what to check first, how to undo. Ids and paths only in brackets, never as the explanation.
    /// </summary>
    public static class OwnerAskText
    {
        public const string GrantNote = "(Dòng cuối là lệnh sẽ chạy. Đoạn GAME_OWNER_GRANT=… là mã dùng một lần do gate gắn vào để chứng minh bạn đã bấm Yes: không cần đọc.)";

        private sealed class Card
        {
            public string Question, What, Content, Now, Yes, Check, Undo;
            public string No = "Không có gì thay đổi. (AI được dặn không hỏi lại cùng việc này.)";
            public bool Grant = true;

            public override string ToString()
            {
                var lines = new List<string> { "❓ " + Question, "" };
                void Add(string label, string text)
                {
                    if (!string.IsNullOrWhiteSpace(text))
                        lines.Add(label + " " + text.Replace("\n", "\n    "));
                }
                Add("• Việc này là gì:", What);
                Add("• Nội dung:", Content);
                Add("• Hiện trạng:", Now);
                Add("• Bấm Yes:", Yes);
                Add("• Bấm No:", No);
                Add("• Nên kiểm tra trước:", Check);
                Add("• Hoàn tác:", Undo);
                if (Grant)
                {
                    lines.Add("");
                    lines.Add(GrantNote);
                }
                return string.Join("\n", lines);
            }
        }

        // ------------------------------------------------------------------ owner CLI commands

        public static string TaskApprove(AgentTask t)
        {
            var asked = new List<string>();
            foreach (var f in t.RequestedFeatures)
                asked.Add("sửa feature " + f + " (code, test, asset của nó)");
            foreach (var p in t.RequestedPaths)
                asked.Add("sửa thư mục/file " + p);
            if (t.RequestedTestChanges)
                asked.Add("đổi điều mà các test hiện có kiểm tra (có thể làm một test dễ qua hơn: cần bạn tin lý do)");
            if (t.RequestedCoreMaintenance)
                asked.Add("sửa các file được bảo vệ: luật kiến trúc và công cụ kiểm tra (Tools/), cấu hình Claude (.claude/), tài liệu (Docs/, CLAUDE.md)");
            bool first = t.Status == AgentTask.PendingApproval;
            return new Card
            {
                Question = "Cho phép AI làm việc rộng hơn phạm vi mặc định?",
                What = "AI đang làm việc \"" + t.Title + "\" và xin thêm quyền sửa file.",
                Content = (first ? "Phạm vi xin cấp: " + t.ScopeText : "Đang được phép: " + t.ScopeText) +
                          (asked.Count > 0 ? "\nXin thêm:\n- " + string.Join("\n- ", asked) : "") +
                          (string.IsNullOrWhiteSpace(t.RequestReason) ? "" : "\nLý do AI đưa ra: " + t.RequestReason),
                Now = first ? "Việc này chưa bắt đầu; AI chưa sửa được gì." : "AI vẫn làm trong phạm vi cũ cho tới khi bạn đồng ý.",
                Yes = "AI được sửa các phần trên cho tới khi đóng việc này. Mọi file nó sửa vẫn bị kiểm tra luật và test khi kết thúc.",
                Check = "Lý do có khớp với việc bạn giao không? Quyền đổi test hoặc sửa file bảo vệ chỉ nên cho khi bạn hiểu vì sao cần.",
                Undo = "Chạy ./Tools/test-gate.sh task close để đóng việc (quyền hết hiệu lực); sửa đã làm thì xem bằng git diff."
            }.ToString();
        }

        public static string RoadmapAccept(string root, PlanRequirement req)
        {
            var report = RoadmapCli.Report(root);
            var feature = report.Features.FirstOrDefault(f => f.Requirements.Any(r => r.Id == req.Id));
            var state = feature?.Requirements.First(r => r.Id == req.Id);
            return new Card
            {
                Question = "Xác nhận bạn đã TỰ KIỂM TRA yêu cầu này và thấy đạt?",
                What = req.IsManual
                    ? "Yêu cầu này không test tự động được (cảm nhận, nhìn, nghe): chỉ bạn quyết là đạt hay chưa."
                    : "Yêu cầu này có test tự động; bạn đang xác nhận thay cho/ngoài kết quả test.",
                Content = "\"" + req.Text + "\"" + (feature != null ? "\nThuộc tính năng: " + feature.Title + " [" + req.Id + "]" : " [" + req.Id + "]"),
                Now = state == null ? null : StatusText(state.Status) + (state.Tests.Count > 0 ? " (" + state.Tests.Count + " test liên quan)" : ""),
                Yes = "Yêu cầu được tính là ĐẠT cho đúng câu chữ này (ghi vào Docs/Plan/accepted.txt). Nếu câu chữ đổi sau này, nó tự quay về chờ bạn xem lại.",
                Check = "Mở game (hoặc Tools > Game > UI > View Preview) và tự xem đúng điều câu trên mô tả, trên kích thước điện thoại.",
                Undo = "Xóa dòng " + req.Id + " trong Docs/Plan/accepted.txt (bạn tự sửa)."
            }.ToString();
        }

        public static string ArtAccept(SlotState s)
        {
            return new Card
            {
                Question = "Duyệt hình ảnh/hiệu ứng này là đạt (APPROVED)?",
                What = "Gate đã gắn art vào ô \"" + s.Id + "\". Gate chỉ đo được kích thước, tâm, thời lượng; đẹp hay không là bạn quyết.",
                Content = "File đang dùng: " + s.BoundFile + " (" + (s.BoundAs == "final" ? "bản chính thức" : "bản thử") + (s.Version != null ? ", version " + s.Version : "") + ")" +
                          (string.IsNullOrWhiteSpace(s.Detail) ? "" : "\n" + s.Detail),
                Now = "Kiểm tra tự động: " + (s.Gate == "passed" ? "đạt" : s.Gate == "failed" ? "KHÔNG đạt" : "chưa chạy (cần Unity)") +
                      (s.Violations.Count > 0 ? " · " + string.Join("; ", s.Violations) : "") + (s.Measured != null ? " · đo được: " + s.Measured : ""),
                Yes = "Đúng nội dung file hiện tại được ghi là đã duyệt. Ai sửa file đó sau này thì nó tự thành \"cần duyệt lại\".",
                Check = "Mở Dashboard/Data/art/briefs/" + s.Id + ".compare.html (sinh bằng: ./Tools/test-gate.sh art compare " + s.Id +
                        "; ảnh từ Tools > Game > Art > Snapshot): các level cạnh nhau, khung contract vẽ đè, và trong game trên từng máy." +
                        " Hoặc chơi thử / View Preview." +
                        (s.Gate == "failed" ? " Kiểm tra tự động đang KHÔNG đạt: thường nên bấm No." : ""),
                Undo = "Xóa dòng " + s.Id + " " + s.Hash + " trong Docs/Plan/art-accepted.txt."
            }.ToString();
        }

        public static string CrDecide(CrEntry c, bool approve, string why)
        {
            string kind = c.Kind != null && c.Kind.EndsWith("meaning", StringComparison.Ordinal) ? "đổi hành vi/yêu cầu của " + c.To + " (không đổi giao diện code)"
                : c.Kind != null && c.Kind.EndsWith("breaking", StringComparison.Ordinal) ? "đổi giao diện code công khai của " + c.Module + " theo cách có thể làm hỏng chỗ đang dùng"
                : "thêm vào giao diện code công khai của " + c.Module;
            return new Card
            {
                Question = (approve ? "ĐỒNG Ý" : "TỪ CHỐI") + " yêu cầu thay đổi giữa hai phần của game?",
                What = "Phần " + c.From + " cần phần " + c.To + " thay đổi; nó không được tự sửa phần của người khác nên phải xin. Loại: " + kind + ".",
                Content = "\"" + c.Text + "\"" + (c.Api.Count > 0 ? "\nDòng giao diện code xin thêm/bớt:\n- " + string.Join("\n- ", c.Api) : "") +
                          (c.Reason != null ? "\nÝ kiến của phiên phân xử: " + c.Reason : "") + " [" + c.Id + "]",
                Now = "Đang chờ bạn quyết.",
                Yes = approve
                    ? "Việc này thành một yêu cầu của " + c.To + ": một phiên AI làm " + c.To + " sẽ thực hiện nó (có test)."
                    : "Bên " + c.From + " phải đổi cách làm hoặc xin lại theo cách khác.",
                Check = "Thay đổi này có hợp với ý đồ game của bạn không? Lý do bạn ghi: " + (string.IsNullOrWhiteSpace(why) ? "(không có)" : "\"" + why.Trim() + "\""),
                Undo = approve ? "Khó hoàn tác sau khi đã làm; trước đó có thể xin bỏ (cr withdraw) hoặc sửa plan." : "Bên kia có thể xin lại (task need)."
            }.ToString();
        }

        public static string TestsUnseal(PlanRequirement req, SealState s, string why)
        {
            return new Card
            {
                Question = "Gỡ niêm phong bộ test của một yêu cầu? (biện pháp cuối)",
                What = "Bộ test này đã được một phiên AI viết và niêm phong để phiên khác không được sửa cho dễ qua. Gỡ niêm phong thì nó không còn được tính.",
                Content = (req != null ? "\"" + req.Text + "\"" : s.Requirement) + " [" + s.Requirement + "]" +
                          "\nLý do gỡ: \"" + why.Trim() + "\"",
                Now = (s.Verified ? "Test đã từng chạy qua." : "Test chưa từng chạy qua.") + (s.DisputeCount > 0 ? " Đã bị phản đối " + s.DisputeCount + " lần." : ""),
                Yes = "Yêu cầu quay về \"chưa có test được niêm phong\": một phiên AI phải viết/niêm phong lại test trước khi nó được tính là đạt.",
                Check = "Thường nên dùng phân xử (tests rule) thay vì gỡ. Chỉ gỡ khi test sai mà không còn cách nào khác.",
                Undo = "Không hoàn tác trực tiếp: phải niêm phong lại (tests seal)."
            }.ToString();
        }

        public static string TestsGrandfather(List<string> items)
        {
            return new Card
            {
                Question = "Coi các test hiện có là đã niêm phong (làm MỘT lần khi bắt đầu dùng cơ chế niêm phong)?",
                What = "Các yêu cầu đã có test từ trước sẽ được tính như đã niêm phong (người viết = legacy), để không bị tụt hết thành \"chưa niêm phong\".",
                Content = items.Count + " yêu cầu:\n- " + string.Join("\n- ", items.Take(20)) + (items.Count > 20 ? "\n… và " + (items.Count - 20) + " yêu cầu khác" : ""),
                Yes = "Các yêu cầu trên được tính bằng test hiện tại; sau này sửa các test đó sẽ bị phát hiện.",
                Check = "Chỉ làm một lần. Nếu đã chạy trước đây thì bấm No.",
                Undo = "Gỡ từng cái bằng tests unseal."
            }.ToString();
        }

        // ------------------------------------------------------------------ baselines

        public static string GoldenWrite(string root)
        {
            var files = Baselines(root, "/Tests/Replays/");
            return new Card
            {
                Question = "Ghi lại các bản chơi mẫu (golden replay) theo luật chơi MỚI?",
                What = "Golden replay là các ván chơi mẫu được ghi lại; test so từng bước với chúng để phát hiện luật chơi đổi ngoài ý muốn.",
                Content = "Sẽ ghi đè " + (files.Count == 0 ? "các file replay" : files.Count + " file: " + string.Join(", ", files.Take(5).Select(Path.GetFileName)) + (files.Count > 5 ? ", …" : "")),
                Now = Changed(root, f => f.Contains("/Logic/"), "code luật chơi (Logic/)"),
                Yes = "Kết quả mới thành chuẩn. Chỉ đúng khi bạn CỐ Ý đổi gameplay (và đã tăng SimVersion).",
                No = "Không có gì thay đổi; test replay vẫn đỏ để báo luật chơi đã đổi.",
                Check = "Bạn có chủ ý đổi luật chơi không? Nếu không, đây là lỗi: bấm No và bảo AI sửa code.",
                Undo = "git checkout -- trên thư mục Tests/Replays/ (trước khi commit), rồi xem lại bằng git diff.",
                Grant = false
            }.ToString();
        }

        public static string LayoutWrite(string root, IEnumerable<string> args)
        {
            var views = args.Select(a => a.Trim('"')).Where(a => a.Contains("View:")).SelectMany(a => a.Split(';')).Where(a => a.StartsWith("View:", StringComparison.Ordinal))
                .Select(a => a.Substring(5)).ToList();
            return new Card
            {
                Question = "Ghi lại ảnh chụp bố cục (layout) của giao diện theo bố cục MỚI?",
                What = "Mỗi màn hình có một bản ghi vị trí/kích thước từng phần tử; test so với bản ghi này để bắt lỗi giao diện bị xô lệch.",
                Content = views.Count > 0 ? "Màn hình: " + string.Join(", ", views) : "MỌI màn hình (không chỉ định màn nào)",
                Now = Changed(root, f => f.Contains("/Views/") || f.EndsWith("Layout.cs", StringComparison.Ordinal) || f.EndsWith(".prefab", StringComparison.Ordinal), "giao diện (Views/, *Layout.cs, prefab)"),
                Yes = "Bố cục hiện tại thành chuẩn mới.",
                No = "Không có gì thay đổi; test layout vẫn báo khác.",
                Check = "Bạn có chủ ý đổi giao diện không? Xem trước: Unity > Tools > Game > UI > View Preview. Không chủ ý thì bấm No.",
                Undo = "git checkout -- trên thư mục Tests/Layout/ (trước khi commit), rồi xem lại bằng git diff.",
                Grant = false
            }.ToString();
        }

        /// <summary>Files of this kind changed since the last commit (git), so the owner sees whether a rewrite is plausible.</summary>
        private static string Changed(string root, Func<string, bool> kind, string label)
        {
            var (code, output) = AgentGit.Run(root, "status --porcelain --untracked-files=all");
            if (code != 0)
                return "Không đọc được git để biết " + label + " có đổi không.";
            var files = output.Split('\n').Where(l => l.Length > 3).Select(l => l.Substring(3).Trim().Trim('"'))
                .Where(f => f.StartsWith("UnityProject/Assets/_Game/", StringComparison.Ordinal) && !f.EndsWith(".meta", StringComparison.Ordinal) && kind(f))
                .Select(f => f.Substring("UnityProject/Assets/_Game/".Length)).ToList();
            if (files.Count == 0)
                return "Chưa có file " + label + " nào đổi so với commit gần nhất: nếu test đang báo lệch thì nguyên nhân nằm chỗ khác, nên bấm No và hỏi AI.";
            return files.Count + " file " + label + " đã đổi so với commit gần nhất:\n- " + string.Join("\n- ", files.Take(8)) + (files.Count > 8 ? "\n… +" + (files.Count - 8) : "");
        }

        private static List<string> Baselines(string root, string segment)
        {
            string game = Path.Combine(root, "UnityProject", "Assets", "_Game");
            if (!Directory.Exists(game))
                return new List<string>();
            return Directory.GetFiles(game, "*", SearchOption.AllDirectories).Select(f => f.Replace('\\', '/'))
                .Where(f => f.Contains(segment) && !f.EndsWith(".meta", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        }

        private static string StatusText(string s)
        {
            switch (s)
            {
                case ReqStatus.Verified: return "Test tự động đã chạy qua.";
                case ReqStatus.Accepted: return "Bạn đã xác nhận trước đây.";
                case ReqStatus.NeedsReview: return "Đang chờ bạn tự kiểm tra.";
                case ReqStatus.NoTest: return "Chưa có test nào.";
                case ReqStatus.NotRun: return "Có test nhưng chưa chạy.";
                case ReqStatus.Failing: return "Test đang FAIL.";
                case ReqStatus.Unverified: return "Code đã đổi sau lần test cuối: chưa chắc còn đúng.";
                case ReqStatus.Stale: return "Câu chữ đã đổi sau lần xác nhận trước.";
                default: return s;
            }
        }
    }
}
