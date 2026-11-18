using System.Globalization;

namespace Game.MathQuiz.Simulation
{
    internal enum QuizCommandKind
    {
        Start,
        Answer,
        Next
    }

    /// <summary>
    /// Everything the player can do, as data. Commands are queued and applied at the start of the next tick,
    /// in order; the replay log stores their text form with that tick. Nothing else changes gameplay.
    /// </summary>
    internal readonly struct QuizCommand
    {
        public QuizCommandKind Kind { get; }
        public int Option { get; }

        private QuizCommand(QuizCommandKind kind, int option)
        {
            Kind = kind;
            Option = option;
        }

        public static QuizCommand Start() => new QuizCommand(QuizCommandKind.Start, 0);
        public static QuizCommand Answer(int option) => new QuizCommand(QuizCommandKind.Answer, option);
        public static QuizCommand Next() => new QuizCommand(QuizCommandKind.Next, 0);

        public override string ToString() => Kind == QuizCommandKind.Answer ? "Answer " + Option.ToString(CultureInfo.InvariantCulture) : Kind.ToString();

        public static bool TryParse(string text, out QuizCommand command)
        {
            command = default;
            var parts = (text ?? "").Trim().Split(' ');
            switch (parts[0])
            {
                case "Start" when parts.Length == 1: command = Start(); return true;
                case "Next" when parts.Length == 1: command = Next(); return true;
                case "Answer" when parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var o):
                    command = Answer(o);
                    return true;
                default:
                    return false;
            }
        }
    }
}
