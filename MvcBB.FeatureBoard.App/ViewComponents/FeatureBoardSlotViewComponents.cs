using Microsoft.AspNetCore.Mvc;
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
}
