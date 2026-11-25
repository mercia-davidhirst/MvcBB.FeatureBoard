using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MvcBB.FeatureBoard.App.ViewComponents;
using MvcBB.ModKit;

namespace MvcBB.FeatureBoard.App
{
    public class FeatureBoardAppMod : IAppMod
    {
        public const string ModId = "featureboard";
        public const string HttpClientName = "FeatureBoardApi";

        public string Id => ModId;
        public string Name => "Feature Board";

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            var apiBaseUrl = configuration["Api:BaseUrl"]
                ?? throw new InvalidOperationException("Api:BaseUrl is not configured");

            services.AddHttpClient(HttpClientName, client =>
            {
                client.BaseAddress = new Uri(apiBaseUrl);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                // A hung API should delay a page by seconds, not HttpClient's 100.
                client.Timeout = TimeSpan.FromSeconds(10);
            });

            services.AddScoped<IFeatureBoardService, FeatureBoardService>();
            services.AddSingleton<FeatureBoardRateLimiter>();
        }

        public IEnumerable<ModNavItem> GetNavItems()
        {
            yield return new ModNavItem("Feature Board", "FeatureBoard", "Index", "bi-kanban");
        }

        public IEnumerable<ModSlotContribution> GetSlotContributions()
        {
            yield return new ModSlotContribution(CoreSlots.LayoutHead, typeof(FeatureBoardStylesheetViewComponent));
            yield return new ModSlotContribution(CoreSlots.HomeRight, typeof(FeatureBoardHomeRecentViewComponent));
            // The Administrators' new-request alert. A core without these slots
            // logs and ignores them, and the rest of the mod works as before.
            yield return new ModSlotContribution(CoreSlots.LayoutNavbar, typeof(FeatureBoardRequestsNavViewComponent));
            yield return new ModSlotContribution(CoreSlots.LayoutToasts, typeof(FeatureBoardRequestsToastViewComponent));
        }
    }
}
