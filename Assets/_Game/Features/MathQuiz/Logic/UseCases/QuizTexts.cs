namespace Game.MathQuiz.UseCases
{
    /// <summary>
    /// All user-facing strings of the feature in one place (swap for a localization table later).
    /// </summary>
    internal static class QuizTexts
    {
        public static string Question(int a, int b) => a + " + " + b + " = ?";
        public static string Progress(int number, int total) => "Câu " + number + "/" + total;
        public static string Score(int score) => "Điểm: " + score;

        public const string Correct = "Chính xác!";
        public static string Wrong(int correctAnswer) => "Sai rồi! Đáp án đúng là " + correctAnswer;

        public static string TimeUp(int correctAnswer) => "Hết giờ! Đáp án đúng là " + correctAnswer;
        public static string Timer(int secondsLeft) => secondsLeft + "s";

        public const string Next = "Tiếp";
        public const string SeeResult = "Xem kết quả";

        public const string RoundFinished = "Hoàn thành!";
        public const string Defeated = "Căn cứ đã thất thủ!";
        public static string Lives(int lives) => "Máu: " + lives;
        public const string NewBest = "Kỷ lục mới!";
        public static string Summary(int score, int total, int best) =>
            "Bạn đúng " + score + "/" + total + " câu\nKỷ lục: " + best;

        public const string Restart = "Chơi lại";
    }
}
