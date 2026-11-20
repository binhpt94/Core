using System.Collections.Generic;
using System.Linq;

namespace TestGate.AgentControl
{
    /// <summary>The owner's yes/no dialogs for files that used to be edited by hand: API baselines, vendor and MCP policy.</summary>
    public static class OwnerAskPolicyText
    {
        private const int MaxLines = 15;

        private static string Lines(string sign, List<string> lines) =>
            string.Join("\n", lines.Take(MaxLines).Select(l => sign + " " + l.Trim())) + (lines.Count > MaxLines ? "\n… +" + (lines.Count - MaxLines) + " dòng" : "");

        public static string ApiAccept(ApiBaseline.Change c, List<string> users)
        {
            var content = new List<string>();
            if (c.Removed.Count > 0)
                content.Add("Bỏ hoặc đổi (" + c.Removed.Count + "):\n" + Lines("-", c.Removed));
            if (c.Added.Count > 0)
                content.Add("Thêm (" + c.Added.Count + "):\n" + Lines("+", c.Added));
            return new OwnerAskText.Card
            {
                Question = "Chấp nhận giao diện công khai (API) mới của " + c.Group + "?",
                What = "Đây là danh sách những gì phần khác của game được phép gọi vào " + c.Group + ". Test so code với danh sách này để không ai lỡ tay phá phần khác [" + c.Baseline + "].",
                Content = string.Join("\n", content),
                Now = c.Breaking
                    ? "CÓ PHÁ VỠ: có dòng bị bỏ/đổi, code đang dùng chúng sẽ phải sửa theo" + (users.Count > 0 ? " (đang dùng: " + string.Join(", ", users) + ")." : ".")
                    : "Chỉ THÊM mới: phần khác của game không bị ảnh hưởng.",
                Yes = "Danh sách mới thành chuẩn; test API (ARCH023) xanh lại. Gate kiểm tra lại code ngay lúc chạy: nếu API đã đổi khác những dòng trên thì không ghi gì.",
                No = "Không có gì thay đổi; test API vẫn đỏ cho tới khi code khớp danh sách cũ.",
                Check = c.Breaking ? "Bạn có chủ ý bỏ/đổi các dòng \"-\" không? Nếu không, bấm No và bảo AI giữ API cũ (chỉ thêm, không bỏ)." : "Các dòng \"+\" có khớp việc bạn giao không?",
                Undo = "git checkout -- " + c.Baseline + " (trước khi commit)."
            }.ToString();
        }

        public static string VendorAdd(string line, string tier, List<string> users)
        {
            string tierText = tier == "foundation" ? "dùng được ở mọi nơi, kể cả code luật chơi thuần (như UniTask)"
                : tier == "presentation" ? "chỉ dùng ở lớp Unity (hiển thị, hiệu ứng), không được vào luật chơi (như DOTween)"
                : "SDK bên ngoài (quảng cáo, thanh toán, analytics): chỉ đúng một module adapter được gọi trực tiếp";
            return new OwnerAskText.Card
            {
                Question = "Cho phép dùng thư viện ngoài (vendor) này trong game?",
                What = "Thư viện ngoài chỉ được dùng khi có tên trong danh sách cho phép; danh sách ghi rõ loại và nơi được dùng [Tools/ArchitecturePolicy.txt].",
                Content = "Loại: " + tierText + "\nDòng sẽ thêm: " + line,
                Now = users.Count > 0 ? "Đang được tham chiếu bởi: " + string.Join(", ", users) + " (đang bị chặn vì chưa đăng ký)." : "Chưa có module nào tham chiếu nó.",
                Yes = "Thêm đúng một dòng trên. Thư viện bị khoá theo phiên bản/mã băm: ai sửa nó sau này sẽ bị test bắt.",
                Check = "Bạn có muốn phụ thuộc vào thư viện này (giấy phép, kích thước, cập nhật) không? SDK thì có đúng module adapter không?",
                Undo = "git checkout -- Tools/ArchitecturePolicy.txt (trước khi commit)."
            }.ToString();
        }

        public static string McpAllow(string kind, string pattern, string now)
        {
            string kindText = kind == "read" ? "chỉ đọc, không đổi file (AI gọi tự do, không cần task)"
                : kind == "write" ? "đổi file trong project (cần task; mọi file nó đổi đều bị kiểm tra như AI tự sửa)"
                : "CHẠY CODE TUỲ Ý trong Unity/Blender: nó có thể làm bất cứ gì, kể cả ghi ra ngoài project mà gate không thấy";
            return new OwnerAskText.Card
            {
                Question = kind == "exec" ? "⚠ Cho phép AI dùng công cụ CHẠY CODE TUỲ Ý " + pattern + "?" : "Đổi quyền của công cụ MCP " + pattern + " thành \"" + kind + "\"?",
                What = "Công cụ MCP là các nút AI bấm trong Unity/Blender. Mỗi công cụ được xếp loại để biết có cần kiểm tra không [Tools/mcp-policy.txt].",
                Content = "Loại mới: " + kindText + "\nDòng sẽ thêm: " + kind + " " + pattern,
                Now = "Hiện tại: " + now + ".",
                Yes = "Thêm đúng một dòng trên (dòng cuối thắng các dòng trước).",
                Check = kind == "exec" ? "Chỉ đồng ý nếu không có công cụ cụ thể hơn làm được việc này và bạn tin việc AI định làm." : "Tên công cụ có đúng là việc nó làm không (đọc/ghi)?",
                Undo = "git checkout -- Tools/mcp-policy.txt (trước khi commit)."
            }.ToString();
        }
    }
}
