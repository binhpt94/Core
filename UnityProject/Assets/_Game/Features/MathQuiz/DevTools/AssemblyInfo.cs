using System.Runtime.CompilerServices;

// Fixtures and scenarios use the feature's internal view models and simulation; only this feature's
// Editor tools (view cases, preview window) and tests may use them.
[assembly: InternalsVisibleTo("Game.MathQuiz.Editor")]
[assembly: InternalsVisibleTo("Game.MathQuiz.Tests")]
