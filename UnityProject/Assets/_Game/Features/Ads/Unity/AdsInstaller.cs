using Game.Ads.Contracts;

namespace Game.Ads.Infrastructure
{
    /// <summary>Public face of the Ads feature: Bootstrap gets an IAdsService, never the SDK.</summary>
    public static class AdsInstaller
    {
        public static IAdsService Install(string appKey)
        {
            var sdk = DemoAds.DemoAds.Instance; // vendor singleton: confined to this assembly
            sdk.Initialize(appKey);
            return new DemoAdsAdapter(sdk);
        }
    }
}
