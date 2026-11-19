# Plan: Quiz Defense

<!-- OWNED BY THE HUMAN. Agents read it, never edit it: they propose changes in Docs/Plan/Inbox/ (`test-gate.sh plan propose`).
     Statuses are not written here: they are computed from tests ([Covers("R-..")]) and evidence (`test-gate.sh plan status`).
     Syntax: Docs/PLANNING.md. Feature = a capability a player can see and accept; Requirement = one testable behaviour. -->

## F-QUIZ-CORE  Câu hỏi cộng, chọn 1 trong 3  [P0] [modules: MathQuiz]
- R-QUIZ-OPTIONS  3 đáp án khác nhau, đúng một đáp án đúng, đáp án nhiễu gần và không âm
- R-QUIZ-SHUFFLE  Vị trí đáp án đúng được xáo trộn; cùng seed thì cùng câu hỏi
- R-QUIZ-SCORE-ONCE  Mỗi câu chỉ tính điểm một lần, kể cả bấm đúp
- R-QUIZ-ROUND  Một vòng 10 câu kết thúc bằng tổng kết, chơi lại được

## F-TIMER  Đếm giờ mỗi câu  [P0] [modules: MathQuiz]
- R-TIMER-DEADLINE  Hết giờ đúng tick hạn chót và tính là trả lời sai
- R-TIMER-STOP  Trả lời thì dừng đếm; chạm sau khi hết giờ bị bỏ qua
- R-TIMER-URGENT  3 giây cuối hiển thị khẩn cấp
- R-TIMER-FPS  Kết quả không phụ thuộc FPS
deps: hard F-QUIZ-CORE

## F-ARENA  Slime tấn công căn cứ  [P0] [modules: MathQuiz] [slots: MathQuiz.Slime] [art: approved]
- R-ARENA-ZAP  Trả lời đúng diệt slime gần căn cứ nhất
- R-ARENA-RAGE  Trả lời sai hoặc hết giờ làm slime nổi giận và sinh thêm slime
- R-ARENA-DEFEAT  Slime chạm căn cứ thì mất máu; hết máu thì thua
- R-ARENA-FEEL  Slime di chuyển mượt, dễ nhìn trên điện thoại  [manual]
- R-ARENA-BREATHER  Trả lời đúng thì có ít nhất 5 giây trước khi quái đầu tiên chạm căn cứ  [story]
- R-ARENA-READABLE  Quái nổi giận vẫn đủ chậm để mắt theo kịp  [story]
deps: hard F-QUIZ-CORE · soft F-TIMER

## F-REPLAY  Tái hiện lỗi bằng replay  [P1] [modules: MathQuiz]
- R-REPLAY-BITS  Chơi thật rồi phát lại khớp từng bit
- R-REPLAY-DIVERGE  Replay sai báo đúng tick lệch đầu tiên
- R-REPLAY-GOLDEN  Golden replay không đổi khi gameplay không đổi
deps: hard F-ARENA

## F-BEST-SCORE  Kỷ lục  [P0] [modules: Progress]
- R-BEST-SAVE  Điểm cao hơn trở thành kỷ lục và được lưu; điểm thấp hơn bị bỏ qua
- R-BEST-LOAD  Mở lại game thấy kỷ lục cũ; dữ liệu hỏng được sửa an toàn

## F-SUMMARY  Màn tổng kết vòng  [P1] [modules: MathQuiz]
- R-SUMMARY-BEST  Phá kỷ lục thì hiện "Kỷ lục mới"
- R-SUMMARY-DEFEAT  Thua thì tiêu đề "Căn cứ đã thất thủ"
deps: hard F-QUIZ-CORE · contract F-BEST-SCORE (IBestScoreService)

## F-ADS  Quảng cáo thưởng  [P1] [modules: Ads]
- R-ADS-REWARD  Hiện quảng cáo thưởng qua IAdsService; kết quả trả về trên main thread, hủy được
- R-ADS-NO-FILL  Không có quảng cáo thì báo "không khả dụng", không treo

## F-AD-CONTINUE  Xem quảng cáo để thêm 5 giây  [P1] [modules: MathQuiz] [rush]
Rush: làm phần MathQuiz trước với IAdsService giả; nợ tích hợp được theo dõi tới khi F-ADS xong.
- R-AD-OFFER  Khi hết giờ được mời xem quảng cáo (tối đa 1 lần mỗi vòng)
- R-AD-EXTEND  Xem xong thì thêm 5 giây; hủy hoặc lỗi thì tính là sai
deps: hard F-TIMER · contract F-ADS (IAdsService)

## F-BAT-WAVE  Đợt dơi ban đêm  [P2] [modules: MathQuiz] [slots: MathQuiz.Bat] [art: approved] [art-risk]
Ý đồ: một đợt quái khác hẳn slime để đổi nhịp. Dơi là ứng viên nội dung; design lint hiện báo dơi phá R-ARENA-BREATHER.
- R-BAT-FEAS  Art dơi khả thi: dáng đọc được ở 40–96 canvas unit, có cảm giác bay  [feasibility]
- R-BAT-WAVE  Mỗi vòng có một đợt dơi sau câu thứ 5
- R-BAT-FAIR  Đợt dơi vẫn giữ khoảng thở 5 giây  [story]
deps: hard F-ARENA

## F-PAUSE  Tạm dừng  [P2] [modules: MathQuiz]
- R-PAUSE-FREEZE  Tạm dừng thì không có tick nào chạy, kể cả đồng hồ
- R-PAUSE-RESUME  Tiếp tục đúng trạng thái trước khi dừng
deps: hard F-TIMER

## F-COINS  Xu thưởng khi thắng  [P2] [modules: MathQuiz, Progress] [slots: MathQuiz.CoinFx] [art: approved]
Thắng = qua hết 10 câu của vòng mà căn cứ còn máu. Xu được tích lũy qua các vòng (để sau này dùng ở cửa hàng).
- R-COIN-WIN  Thắng vòng thì nhận xu; thua thì không nhận xu nào
- R-COIN-AMOUNT  Số xu = 10 xu thắng vòng + 1 xu cho mỗi câu trả lời đúng
- R-COIN-ONCE  Mỗi vòng chỉ cộng xu một lần, kể cả bấm đúp; phát replay không cộng xu
- R-COIN-SAVE  Tổng xu được lưu, mở lại game vẫn còn; dữ liệu hỏng được sửa an toàn
- R-COIN-SUMMARY  Màn tổng kết hiện số xu vừa nhận và tổng xu hiện có
- R-COIN-SFX  Nhận xu thì phát âm thanh nhận xu đúng một lần; thua thì không phát
- R-COIN-FX  Nhận xu có hiệu ứng xu bay vào tổng xu, rõ và vui mắt trên điện thoại  [manual]
deps: hard F-QUIZ-CORE · hard F-ARENA · soft F-SUMMARY

## F-SHOP  Cửa hàng skin  [P3] [deferred]
- R-SHOP-BUY  Mua skin bằng điểm
