using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Ads.Contracts;

namespace Game.Ads.Infrastructure
{
    /// <summary>
    /// The ONLY place that touches the vendor SDK (ARCH094). It converts the SDK's callback style into
    /// the contract's shape: callback → UniTaskCompletionSource → cancellation → back to the main thread.
    /// </summary>
    internal sealed class DemoAdsAdapter : IAdsService
    {
        private readonly DemoAds.DemoAds _sdk;

        public DemoAdsAdapter(DemoAds.DemoAds sdk) => _sdk = sdk;

        public async UniTask<bool> ShowRewardedAsync(CancellationToken cancellationToken)
        {
            var completion = new UniTaskCompletionSource<bool>();
            _sdk.ShowRewarded(watched => completion.TrySetResult(watched)); // may arrive on a background thread

            using (cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken)))
            {
                bool watched = await completion.Task;
                await UniTask.SwitchToMainThread(cancellationToken); // vendors call back off the main thread
                return watched;
            }
        }
    }
}
