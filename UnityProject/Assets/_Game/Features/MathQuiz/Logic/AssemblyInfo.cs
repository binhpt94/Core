using System.Runtime.CompilerServices;

// Logic is feature-private: only this feature's Unity layer and tests may see it (rule ARCH021).
// Other features use Game.<Feature>.Contracts; Bootstrap uses the public Installer in the Unity layer.
[assembly: InternalsVisibleTo("Game.MathQuiz.Unity")]
[assembly: InternalsVisibleTo("Game.MathQuiz.Tests")]
[assembly: InternalsVisibleTo("Game.MathQuiz.Editor")]   // view cases: create the presenter for previews
[assembly: InternalsVisibleTo("Game.MathQuiz.DevTools")] // fixtures & scenarios (Editor + GAME_DEVTOOLS builds only)
