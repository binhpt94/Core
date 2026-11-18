using System.Runtime.CompilerServices;

// Logic is feature-private (rule ARCH021). Others use Game.Progress.Contracts.IBestScoreService.
[assembly: InternalsVisibleTo("Game.Progress.Unity")]
[assembly: InternalsVisibleTo("Game.Progress.Tests")]
