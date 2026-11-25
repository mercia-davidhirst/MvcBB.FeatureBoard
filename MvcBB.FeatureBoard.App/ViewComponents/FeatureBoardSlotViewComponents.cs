using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MvcBB.ModKit;

namespace MvcBB.FeatureBoard.App.ViewComponents
{
    // View components this mod renders into core's view slots (see
    // FeatureBoardAppMod.GetSlotContributions). Core only invokes them while
    // the mod is enabled. The board is for signed-in members only, so guests
    // never get any output. Views use explicit paths under Views/FeatureBoard/
    // so they can't collide with another assembly's views.

    /// <summary>layout.head: the mod's stylesheet, served from the DLL at /_mods/featureboard/.</summary>
    public class FeatureBoardStylesheetViewComponent : ViewComponent
    {
        public IViewComponentResult Invoke(PageSlotContext context) =>
            context.ViewerIsSignedIn
                ? View("~/Views/FeatureBoard/Slots/Stylesheet.cshtml")
                : Content(string.Empty);
    }

    /// <summary>home.right: the newest feature requests, for every signed-in member.</summary>
    public class FeatureBoardHomeRecentViewComponent : ViewComponent
    {
        public const int Count = 5;
        private const string CacheKey = "mods:featureboard:home-recent";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

        private readonly IFeatureBoardService _service;
        private readonly IMemoryCache _cache;
        private readonly ILogger<FeatureBoardHomeRecentViewComponent> _logger;

        public FeatureBoardHomeRecentViewComponent(IFeatureBoardService service, IMemoryCache cache,
            ILogger<FeatureBoardHomeRecentViewComponent> logger)
        {
            _service = service;
            _cache = cache;
            _logger = logger;
        }

        public async Task<IViewComponentResult> InvokeAsync(PageSlotContext context)
        {
            if (!context.ViewerIsSignedIn)
                return Content(string.Empty);

            // Every member can see every request, so one cached list serves
            // everyone for a minute. Null means the API is older than the App
            // and has no recent list: that's cached too, and shows nothing.
            if (!_cache.TryGetValue(CacheKey, out RecentRequests? recent) || recent == null)
            {
                try
                {
                    recent = new RecentRequests(await _service.GetRecentRequestsAsync(Count));
                    _cache.Set(CacheKey, recent, CacheDuration);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FeatureBoardServiceException)
                {
                    _logger.LogWarning(ex, "Feature Board: couldn't load the newest requests for the home page.");
                    return Content(string.Empty);
                }
            }

            return recent.Items == null
                ? Content(string.Empty)
                : View("~/Views/FeatureBoard/Slots/HomeRecent.cshtml", recent.Items);
        }

        private sealed record RecentRequests(IReadOnlyList<FeatureRequestDto>? Items);
    }

    /// <summary>
    /// layout.navbar: Administrators' icon with a badge counting the requests
    /// waiting in Requested, linking to that column. Kept up to date by
    /// featureboard-alert.js, like core's open-reports flag.
    /// </summary>
    public class FeatureBoardRequestsNavViewComponent : ViewComponent
    {
        private readonly IFeatureBoardService _service;

        public FeatureBoardRequestsNavViewComponent(IFeatureBoardService service) => _service = service;

        public async Task<IViewComponentResult> InvokeAsync(PageSlotContext context)
        {
            if (!context.ViewerIsSignedIn || !User.IsInRole("Administrator"))
                return Content(string.Empty);
            var count = await RequestedCount.GetAsync(HttpContext, _service);
            return View("~/Views/FeatureBoard/Slots/RequestsNav.cshtml", count ?? 0);
        }
    }

    /// <summary>
    /// layout.toasts: the Administrators' "new feature request" toast, and the
    /// script that polls for the count. The page's count is the script's
    /// baseline, so it only toasts for requests made after the page loaded.
    /// </summary>
    public class FeatureBoardRequestsToastViewComponent : ViewComponent
    {
        private readonly IFeatureBoardService _service;

        public FeatureBoardRequestsToastViewComponent(IFeatureBoardService service) => _service = service;

        public async Task<IViewComponentResult> InvokeAsync(PageSlotContext context)
        {
            if (!context.ViewerIsSignedIn || !User.IsInRole("Administrator"))
                return Content(string.Empty);
            var count = await RequestedCount.GetAsync(HttpContext, _service);
            // No baseline when the API couldn't be reached: the script then takes
            // its first poll as the baseline instead of toasting for every request.
            return View("~/Views/FeatureBoard/Slots/RequestsToast.cshtml", count);
        }
    }

    /// <summary>The Requested column's count, loaded at most once per page for both alert slots.</summary>
    internal static class RequestedCount
    {
        private static readonly object ItemsKey = new();

        public static async Task<int?> GetAsync(HttpContext httpContext, IFeatureBoardService service)
        {
            if (httpContext.Items.TryGetValue(ItemsKey, out var cached))
                return (int?)cached;

            int? count;
            try
            {
                count = await service.GetRequestedCountAsync();
            }
            catch (FeatureBoardServiceException)
            {
                // Never break the layout over the badge.
                count = null;
            }
            httpContext.Items[ItemsKey] = count;
            return count;
        }
    }
}
