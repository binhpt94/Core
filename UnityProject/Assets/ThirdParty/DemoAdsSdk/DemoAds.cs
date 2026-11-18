// FAKE third-party ads SDK used to demonstrate the vendor boundary. Written the way many SDKs are:
// a global singleton, mutable static state and callbacks on a background thread. None of that is
// allowed in game code, but vendor code lives outside Assets/_Game, so it is not scanned by the strict
// rules. What IS enforced: it is registered + hash-pinned (ARCH093) and only Game.Ads.Unity may call it (ARCH094).
using System;
using System.Threading;

namespace DemoAds
{
    public sealed class DemoAds
    {
        public static DemoAds Instance { get; } = new DemoAds();
        public static bool Initialized;

        public void Initialize(string appKey) => Initialized = !string.IsNullOrEmpty(appKey);

        /// <summary>Shows a rewarded ad. The callback arrives LATER and on a BACKGROUND thread.</summary>
        public void ShowRewarded(Action<bool> onFinished)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(300);
                onFinished(Initialized);
            });
        }
    }
}
