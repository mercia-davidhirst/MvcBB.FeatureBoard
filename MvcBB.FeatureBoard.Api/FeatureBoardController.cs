using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MvcBB.ModKit;
using MvcBB.Shared.Interfaces;
using MvcBB.Shared.Services;

namespace MvcBB.FeatureBoard.Api
{
    /// <summary>
    /// The board's HTTP endpoints. Every endpoint requires a signed-in member;
    /// who may do what is decided by <see cref="FeatureBoardAccess"/>, here,
    /// not by which buttons the App shows. Likes, comments and the author's
    /// additional information are refused unless the request is in Requested,
    /// and the repository checks that again in the same transaction as the
    /// write. Only Administrators move requests between columns.
    ///
    /// Core's word filter is applied to every piece of member-written text
    /// (titles, descriptions, additions, comments, status comments) before
    /// it's saved, the same way core filters posts.
    /// </summary>
    [Mod(FeatureBoardMod.ModId)]
    [ApiController]
    [Route("api/mods/featureboard")]
    [Authorize]
    public class FeatureBoardController : ControllerBase
    {
        public const int DefaultPerColumn = 25;
        public const int MaxPerColumn = 100;
        public const int DefaultPageSize = 25;
        public const int MaxPageSize = 100;
        private const int MaxPage = 100_000;

        private const string ClosedMessage =
            "This request is no longer open: likes, comments and additions are only possible while it's in Requested.";

        private readonly IFeatureBoardRepository _repository;
        private readonly IWordFilterRepository _wordFilters;

        public FeatureBoardController(IFeatureBoardRepository repository, IWordFilterRepository wordFilters)
        {
            _repository = repository;
            _wordFilters = wordFilters;
        }

        // ================= Board =================

        /// <summary>Every column with its first <paramref name="perColumn"/> requests, and which of them the viewer liked.</summary>
        [HttpGet("board")]
        public ActionResult<Board> GetBoard(int perColumn = DefaultPerColumn)
        {
            if (Viewer is not (int userId, _)) return Unauthorized();

            var columns = _repository.GetBoard(Math.Clamp(perColumn, 1, MaxPerColumn));
            var liked = _repository.GetLikedIds(userId,
                columns.Where(c => c.Status == FeatureStatus.Requested).SelectMany(c => c.Items).Select(r => r.Id));
            return Ok(new Board { Columns = columns, LikedIds = liked });
        }

        /// <summary>One column, paged.</summary>
        [HttpGet("requests")]
        public ActionResult<PagedRequests> GetByStatus(FeatureStatus status, int page = 1, int pageSize = DefaultPageSize)
        {
            if (!FeatureStatuses.IsDefined(status)) return BadRequest(new { message = "Unknown status." });
            return Ok(_repository.GetByStatus(status, Math.Clamp(page, 1, MaxPage), Math.Clamp(pageSize, 1, MaxPageSize)));
        }

        /// <summary>The signed-in member's own requests, newest first.</summary>
        [HttpGet("requests/mine")]
        public ActionResult<IEnumerable<FeatureRequest>> Mine()
        {
            if (Viewer is not (int userId, _)) return Unauthorized();
            return Ok(_repository.GetByAuthor(userId));
        }

        // ================= Requests =================

        [HttpGet("requests/{id:int}")]
        public ActionResult<RequestDetails> GetRequest(int id)
        {
            if (Viewer is not (int userId, bool isAdmin)) return Unauthorized();
            var request = _repository.GetRequest(id);
            if (request == null) return RequestNotFound();

            var comments = _repository.GetComments(id);
            foreach (var comment in comments)
                comment.CanDelete = FeatureBoardAccess.CanDeleteComment(request, comment, userId, isAdmin);

            var additions = _repository.GetAdditions(id);
            var canDeleteAdditions = FeatureBoardAccess.CanDeleteAddition(request, userId, isAdmin);
            foreach (var addition in additions)
                addition.CanDelete = canDeleteAdditions;

            return Ok(new RequestDetails
            {
                Request = request,
                Additions = additions,
                Comments = comments,
                History = _repository.GetHistory(id),
                LikedByCurrentUser = _repository.HasLiked(id, userId),
                Permissions = FeatureBoardAccess.For(request, userId, isAdmin)
            });
        }

        /// <summary>Adds a request to the Requested column.</summary>
        [HttpPost("requests")]
        public ActionResult<FeatureRequest> CreateRequest(CreateRequestRequest body)
        {
            if (Viewer is not (int userId, _)) return Unauthorized();

            var title = Filter(body.Title, FeatureBoardLimits.TitleLength);
            var description = Filter(body.Description, FeatureBoardLimits.DescriptionLength);
            if (title.Length == 0) return BadRequest(new { message = "Please give the request a title." });
            if (description.Length == 0) return BadRequest(new { message = "Please describe the feature." });

            var created = _repository.AddRequest(new FeatureRequest
            {
                AuthorUserId = userId,
                Title = title,
                Description = description
            });
            return CreatedAtAction(nameof(GetRequest), new { id = created.Id }, created);
        }

        /// <summary>
        /// Moves a request to another column (Administrators only), with a
        /// comment explaining why. The comment is optional only when discarding.
        /// </summary>
        [HttpPut("requests/{id:int}/status")]
        public IActionResult ChangeStatus(int id, ChangeStatusRequest body)
        {
            if (Viewer is not (int userId, bool isAdmin)) return Unauthorized();
            var request = _repository.GetRequest(id);
            if (request == null) return RequestNotFound();
            if (!FeatureBoardAccess.CanChangeStatus(isAdmin)) return NotAllowed("Only administrators can change a request's status.");

            var comment = Filter(body.Comment ?? string.Empty, FeatureBoardLimits.StatusCommentLength);
            if (body.Status is not FeatureStatus target) return BadRequest(new { message = "Please choose the column to move it to." });
            var problem = FeatureBoardAccess.ValidateStatusChange(request, target, comment);
            if (problem != null) return BadRequest(new { message = problem });

            var moved = _repository.ChangeStatus(new FeatureStatusChange
            {
                RequestId = id,
                FromStatus = request.Status,
                ToStatus = target,
                ChangedByUserId = userId,
                Comment = comment
            });
            if (!moved)
                return Conflict(new { message = "Someone else changed this request's status just now. Please check it and try again." });

            return Ok(new { status = target });
        }

        /// <summary>Deletes a request and everything on it (its author while it's open, or an Administrator).</summary>
        [HttpDelete("requests/{id:int}")]
        public IActionResult DeleteRequest(int id)
        {
            if (Viewer is not (int userId, bool isAdmin)) return Unauthorized();
            var request = _repository.GetRequest(id);
            if (request == null) return RequestNotFound();
            if (!FeatureBoardAccess.CanDelete(request, userId, isAdmin))
                return NotAllowed(FeatureBoardAccess.IsAuthor(request, userId)
                    ? "Only administrators can delete a request once it has moved on from Requested."
                    : "You can't delete this request.");

            _repository.DeleteRequest(id);
            return NoContent();
        }

        // ================= Additional information =================

        /// <summary>Adds more information to a request (its author, while it's in Requested).</summary>
        [HttpPost("requests/{id:int}/additions")]
        public ActionResult<FeatureAddition> AddAddition(int id, AddAdditionRequest body)
        {
            if (Viewer is not (int userId, _)) return Unauthorized();
            var request = _repository.GetRequest(id);
            if (request == null) return RequestNotFound();
            if (!FeatureBoardAccess.IsAuthor(request, userId))
                return NotAllowed("Only the request's author can add information to it.");
            if (!FeatureBoardAccess.IsOpen(request)) return Closed();

            var content = Filter(body.Content, FeatureBoardLimits.AdditionLength);
            if (content.Length == 0) return BadRequest(new { message = "The additional information is empty." });

            var created = _repository.AddAddition(new FeatureAddition { RequestId = id, Content = content });
            if (created == null) return Closed();
            created.CanDelete = true;
            return Ok(created);
        }

        [HttpDelete("additions/{id:int}")]
        public IActionResult DeleteAddition(int id)
        {
            if (Viewer is not (int userId, bool isAdmin)) return Unauthorized();
            var addition = _repository.GetAddition(id);
            var request = addition == null ? null : _repository.GetRequest(addition.RequestId);
            if (addition == null || request == null) return NotFound(new { message = "Information not found" });
            if (!FeatureBoardAccess.CanDeleteAddition(request, userId, isAdmin))
                return FeatureBoardAccess.IsAuthor(request, userId) ? Closed() : NotAllowed("You can't delete this information.");

            _repository.DeleteAddition(id);
            return Ok(new { requestId = request.Id });
        }

        // ================= Likes and comments =================

        /// <summary>Likes or unlikes a request while it's in Requested. Authors can't like their own.</summary>
        [HttpPut("requests/{id:int}/like")]
        public IActionResult SetLike(int id, SetLikeRequest body)
        {
            if (Viewer is not (int userId, _)) return Unauthorized();
            var request = _repository.GetRequest(id);
            if (request == null) return RequestNotFound();
            if (FeatureBoardAccess.IsAuthor(request, userId))
                return BadRequest(new { message = "You can't like your own request." });
            if (!FeatureBoardAccess.IsOpen(request) || !_repository.SetLike(id, userId, body.Liked)) return Closed();

            var updated = _repository.GetRequest(id);
            return Ok(new { liked = body.Liked, likeCount = updated?.LikeCount ?? 0 });
        }

        /// <summary>Comments on a request while it's in Requested.</summary>
        [HttpPost("requests/{id:int}/comments")]
        public ActionResult<FeatureComment> AddComment(int id, AddCommentRequest body)
        {
            if (Viewer is not (int userId, _)) return Unauthorized();
            var request = _repository.GetRequest(id);
            if (request == null) return RequestNotFound();
            if (!FeatureBoardAccess.CanComment(request)) return Closed();

            var content = Filter(body.Content, FeatureBoardLimits.CommentLength);
            if (content.Length == 0) return BadRequest(new { message = "The comment is empty." });

            var created = _repository.AddComment(new FeatureComment { RequestId = id, UserId = userId, Content = content });
            if (created == null) return Closed();
            created.CanDelete = true;
            return Ok(created);
        }

        /// <summary>Deletes a comment (its writer while the request is open, or an Administrator).</summary>
        [HttpDelete("comments/{id:int}")]
        public IActionResult DeleteComment(int id)
        {
            if (Viewer is not (int userId, bool isAdmin)) return Unauthorized();
            var comment = _repository.GetComment(id);
            var request = comment == null ? null : _repository.GetRequest(comment.RequestId);
            if (comment == null || request == null) return NotFound(new { message = "Comment not found" });
            if (!FeatureBoardAccess.CanDeleteComment(request, comment, userId, isAdmin))
                return comment.UserId == userId ? Closed() : NotAllowed("You can't delete this comment.");

            _repository.DeleteComment(id);
            return Ok(new { requestId = request.Id });
        }

        // ================= Helpers =================

        /// <summary>The signed-in member's id and whether they're an Administrator, or null.</summary>
        private (int UserId, bool IsAdmin)? Viewer =>
            int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) && id > 0
                ? (id, User.IsInRole("Administrator"))
                : null;

        private string Filter(string text, int maxLength)
        {
            var filtered = WordFilterApplier.Apply(text.Trim(), _wordFilters.GetAll()).Trim();
            return filtered.Length > maxLength ? filtered[..maxLength] : filtered;
        }

        private NotFoundObjectResult RequestNotFound() => NotFound(new { message = "Feature request not found" });

        // 409: the request exists and the member could act on it, but not in its current state.
        private ConflictObjectResult Closed() => Conflict(new { message = ClosedMessage });

        private static ObjectResult NotAllowed(string message) =>
            new(new { message }) { StatusCode = StatusCodes.Status403Forbidden };
    }
}
