using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using MvcBB.ModKit;

namespace MvcBB.FeatureBoard.App
{
    /// <summary>
    /// The board's pages. Signed-in members only, everywhere. The API makes
    /// every permission decision (status changes, and whether a request is
    /// still open for likes, comments and additions); the flags it returns
    /// (RequestPermissionsDto) only decide which buttons to show.
    /// </summary>
    [Mod(FeatureBoardAppMod.ModId)]
    [Authorize]
    public class FeatureBoardController : Controller
    {
        public const int BoardPerColumn = 25;
        public const int ColumnPageSize = 25;

        private readonly IFeatureBoardService _service;
        private readonly FeatureBoardRateLimiter _rateLimiter;

        public FeatureBoardController(IFeatureBoardService service, FeatureBoardRateLimiter rateLimiter)
        {
            _service = service;
            _rateLimiter = rateLimiter;
        }

        // ================= Board =================

        /// <summary>
        /// The kanban board: every column, or with <paramref name="mine"/> only
        /// the member's own requests (all of them, in the board's order).
        /// </summary>
        public async Task<IActionResult> Index(bool mine = false)
        {
            var model = new BoardPageModel { CurrentUserId = CurrentUserId ?? 0, PerColumn = BoardPerColumn, Mine = mine };
            try
            {
                model.Board = mine
                    ? BoardDto.FromRequests(await _service.GetMyRequestsAsync())
                    : await _service.GetBoardAsync(BoardPerColumn);
            }
            catch (FeatureBoardServiceException ex)
            {
                TempData["Error"] = ex.Message;
            }
            return View(model);
        }

        /// <summary>Every request in one column, paged.</summary>
        public async Task<IActionResult> Column(FeatureStatus status, int page = 1)
        {
            if (!Enum.IsDefined(status)) return NotFound();
            try
            {
                var results = await _service.GetColumnAsync(status, Math.Max(page, 1), ColumnPageSize);
                return View(new ColumnPageModel { Status = status, Results = results });
            }
            catch (FeatureBoardServiceException ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }
        }

        // ================= Requests =================

        /// <summary>One request: its description, additional information, likes, comments and status history.</summary>
        public async Task<IActionResult> Details(int id)
        {
            try
            {
                var details = await _service.GetRequestAsync(id);
                if (details == null) return NotFound();
                ViewBag.Smilies = await _service.GetSmiliesAsync();
                return View(details);
            }
            catch (FeatureBoardServiceException ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction(nameof(Index));
            }
        }

        [HttpGet]
        public async Task<IActionResult> Create() => await RequestFormAsync(new RequestFormModel());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(RequestFormModel form)
        {
            if (!ModelState.IsValid) return await RequestFormAsync(form);
            if (CurrentUserId is not int userId || !_rateLimiter.TryAcquire(FeatureBoardRateLimiter.LimitedAction.Request, userId))
            {
                ModelState.AddModelError(string.Empty, "You've made a lot of requests in a short time. Please wait a few minutes and try again.");
                return await RequestFormAsync(form);
            }

            try
            {
                var created = await _service.CreateRequestAsync(form.Title, form.Description);
                TempData["Success"] = "Thanks! Your request is on the board for other members to like and comment on.";
                return RedirectToAction(nameof(Details), new { id = created.Id });
            }
            catch (FeatureBoardServiceException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return await RequestFormAsync(form);
            }
        }

        /// <summary>Administrators: moves the request to another column, with a comment.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> ChangeStatus(int id, FeatureStatus? status, string? comment) => RunAsync(
            async () =>
            {
                comment = comment?.Trim();
                if (comment is { Length: > FeatureBoardLimits.StatusCommentLength })
                    throw new FeatureBoardServiceException("The comment can be up to 2000 characters.");

                if (status is not FeatureStatus target || !Enum.IsDefined(target))
                    throw new FeatureBoardServiceException("Please choose the column to move it to.");

                await _service.ChangeStatusAsync(id, target, comment);
                TempData["Success"] = $"Moved to {FeatureStatusDisplay.Name(target)}.";
            },
            () => RedirectToAction(nameof(Details), new { id }));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                await _service.DeleteRequestAsync(id);
                TempData["Success"] = "Request deleted.";
                return RedirectToAction(nameof(Index));
            }
            catch (FeatureBoardServiceException ex)
            {
                TempData["Error"] = ex.Message;
                return ex.NotFound ? RedirectToAction(nameof(Index)) : RedirectToAction(nameof(Details), new { id });
            }
        }

        // ================= Additional information =================

        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> AddInformation(int id, string? content) => RunAsync(
            async () =>
            {
                content = content?.Trim();
                if (string.IsNullOrEmpty(content))
                    throw new FeatureBoardServiceException("The additional information is empty.");
                if (content.Length > FeatureBoardLimits.AdditionLength)
                    throw new FeatureBoardServiceException("Additional information can be up to 4000 characters.");
                if (CurrentUserId is not int userId || !_rateLimiter.TryAcquire(FeatureBoardRateLimiter.LimitedAction.Comment, userId))
                    throw new FeatureBoardServiceException("You've posted a lot in a short time. Please wait a few minutes and try again.");

                await _service.AddInformationAsync(id, content);
            },
            () => Redirect(Url.Action(nameof(Details), new { id }) + "#information"));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteInformation(int id, int requestId)
        {
            try
            {
                await _service.DeleteInformationAsync(id);
                TempData["Success"] = "Information deleted.";
            }
            catch (FeatureBoardServiceException ex)
            {
                TempData["Error"] = ex.Message;
            }
            return Redirect(Url.Action(nameof(Details), new { id = requestId }) + "#information");
        }

        // ================= Likes and comments =================

        /// <summary>Likes or unlikes, then goes back to the board or the request, whichever it came from.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> Like(int id, bool liked, bool fromBoard = false) => RunAsync(
            () => _service.SetLikeAsync(id, liked),
            () => fromBoard
                ? Redirect(Url.Action(nameof(Index)) + "#request-" + id)
                : RedirectToAction(nameof(Details), new { id }));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public Task<IActionResult> AddComment(int id, string? content) => RunAsync(
            async () =>
            {
                content = content?.Trim();
                if (string.IsNullOrEmpty(content))
                    throw new FeatureBoardServiceException("The comment is empty.");
                if (content.Length > FeatureBoardLimits.CommentLength)
                    throw new FeatureBoardServiceException("Comments can be up to 2000 characters.");
                if (CurrentUserId is not int userId || !_rateLimiter.TryAcquire(FeatureBoardRateLimiter.LimitedAction.Comment, userId))
                    throw new FeatureBoardServiceException("You've posted a lot in a short time. Please wait a few minutes and try again.");

                await _service.AddCommentAsync(id, content);
            },
            () => Redirect(Url.Action(nameof(Details), new { id }) + "#comments"));

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteComment(int id, int requestId)
        {
            try
            {
                await _service.DeleteCommentAsync(id);
                TempData["Success"] = "Comment deleted.";
            }
            catch (FeatureBoardServiceException ex)
            {
                TempData["Error"] = ex.Message;
            }
            return Redirect(Url.Action(nameof(Details), new { id = requestId }) + "#comments");
        }

        // ================= New-request alert =================

        /// <summary>
        /// Administrators: how many requests are waiting in Requested. Polled by
        /// featureboard-alert.js to refresh the navbar badge and show the toast
        /// when a new request comes in, the same way core's open-reports alert works.
        /// </summary>
        [HttpGet]
        [Authorize(Roles = "Administrator")]
        public async Task<IActionResult> RequestedCount()
        {
            try
            {
                return Json(new { count = await _service.GetRequestedCountAsync() });
            }
            catch (FeatureBoardServiceException)
            {
                // Not a zero: the script skips this tick rather than treating
                // the next real count as a new request and toasting for it.
                return StatusCode(503);
            }
        }

        // ================= Helpers =================

        /// <summary>
        /// Any API failure an action didn't handle itself becomes a message on
        /// the board rather than an error page.
        /// </summary>
        public override void OnActionExecuted(ActionExecutedContext context)
        {
            if (context.Exception is FeatureBoardServiceException ex && !context.ExceptionHandled)
            {
                TempData["Error"] = ex.Message;
                context.Result = RedirectToAction(nameof(Index));
                context.ExceptionHandled = true;
            }
            base.OnActionExecuted(context);
        }

        /// <summary>Runs an action that ends in a redirect, showing any API message on the next page.</summary>
        private async Task<IActionResult> RunAsync(Func<Task> action, Func<IActionResult> redirect)
        {
            try
            {
                await action();
            }
            catch (FeatureBoardServiceException ex)
            {
                TempData["Error"] = ex.Message;
                if (ex.NotFound) return RedirectToAction(nameof(Index));
            }
            return redirect();
        }

        private async Task<IActionResult> RequestFormAsync(RequestFormModel form)
        {
            ViewBag.Smilies = await _service.GetSmiliesAsync();
            return View("RequestForm", form);
        }

        private int? CurrentUserId =>
            int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    }
}
