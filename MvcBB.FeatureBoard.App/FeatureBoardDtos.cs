using System.ComponentModel.DataAnnotations;

namespace MvcBB.FeatureBoard.App
{
    // Mirrors the shapes returned/accepted by MvcBB.FeatureBoard.Api - duplicated
    // rather than shared, since a mod's two tiers share nothing beyond
    // MvcBB.ModKit and MvcBB.Shared.

    /// <summary>Size limits. Must match the API's FeatureBoardLimits.</summary>
    public static class FeatureBoardLimits
    {
        public const int TitleLength = 200;
        public const int DescriptionLength = 4000;
        public const int AdditionLength = 4000;
        public const int CommentLength = 2000;
        public const int StatusCommentLength = 2000;
    }

    /// <summary>Must match the API's FeatureStatus numbers.</summary>
    public enum FeatureStatus
    {
        Discarded = 0,
        Requested = 1,
        Todo = 2,
        InProgress = 3,
        Released = 4
    }

    public static class FeatureStatusDisplay
    {
        /// <summary>The board's columns, left to right.</summary>
        public static readonly IReadOnlyList<FeatureStatus> BoardOrder = new[]
        {
            FeatureStatus.Discarded,
            FeatureStatus.Requested,
            FeatureStatus.Todo,
            FeatureStatus.InProgress,
            FeatureStatus.Released
        };

        public static string Name(FeatureStatus status) => status switch
        {
            FeatureStatus.Discarded => "Discarded",
            FeatureStatus.Requested => "Requested",
            FeatureStatus.Todo => "To-do",
            FeatureStatus.InProgress => "In progress",
            FeatureStatus.Released => "Released",
            _ => status.ToString()
        };

        /// <summary>Bootstrap colour name for the status's badge and column accent.</summary>
        public static string Colour(FeatureStatus status) => status switch
        {
            FeatureStatus.Discarded => "secondary",
            FeatureStatus.Requested => "primary",
            FeatureStatus.Todo => "info",
            FeatureStatus.InProgress => "warning",
            FeatureStatus.Released => "success",
            _ => "secondary"
        };

        public static string Icon(FeatureStatus status) => status switch
        {
            FeatureStatus.Discarded => "bi-x-circle",
            FeatureStatus.Requested => "bi-lightbulb",
            FeatureStatus.Todo => "bi-list-check",
            FeatureStatus.InProgress => "bi-hammer",
            FeatureStatus.Released => "bi-rocket-takeoff",
            _ => "bi-circle"
        };

        /// <summary>A line under each column's heading.</summary>
        public static string Hint(FeatureStatus status) => status switch
        {
            FeatureStatus.Discarded => "Not going ahead",
            FeatureStatus.Requested => "Open for likes and comments",
            FeatureStatus.Todo => "Accepted, waiting to start",
            FeatureStatus.InProgress => "Being worked on",
            FeatureStatus.Released => "Live on the site",
            _ => string.Empty
        };
    }

    public class FeatureRequestDto
    {
        public int Id { get; set; }
        public int AuthorUserId { get; set; }
        public string AuthorUsername { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public FeatureStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime StatusChangedAt { get; set; }
        public int LikeCount { get; set; }
        public int CommentCount { get; set; }
    }

    public class FeatureAdditionDto
    {
        public int Id { get; set; }
        public int RequestId { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool CanDelete { get; set; }
    }

    public class FeatureCommentDto
    {
        public int Id { get; set; }
        public int RequestId { get; set; }
        public int UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public bool CanDelete { get; set; }
    }

    public class FeatureStatusChangeDto
    {
        public int Id { get; set; }
        public int RequestId { get; set; }
        public FeatureStatus FromStatus { get; set; }
        public FeatureStatus ToStatus { get; set; }
        public int ChangedByUserId { get; set; }
        public string ChangedByUsername { get; set; } = string.Empty;
        public string Comment { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public class BoardColumnDto
    {
        public FeatureStatus Status { get; set; }
        public List<FeatureRequestDto> Items { get; set; } = new();
        public int TotalCount { get; set; }
    }

    public class BoardDto
    {
        public List<BoardColumnDto> Columns { get; set; } = new();
        public List<int> LikedIds { get; set; } = new();

        /// <summary>
        /// A board made from a given set of requests (the member's own), every
        /// one of them shown, sorted the way the API sorts the full board.
        /// </summary>
        public static BoardDto FromRequests(IEnumerable<FeatureRequestDto> requests)
        {
            var list = requests.ToList();
            return new BoardDto
            {
                Columns = FeatureStatusDisplay.BoardOrder.Select(status =>
                {
                    var items = list.Where(r => r.Status == status)
                        .OrderByDescending(r => status == FeatureStatus.Requested ? r.LikeCount : 0)
                        .ThenByDescending(r => status == FeatureStatus.Requested ? r.CreatedAt : r.StatusChangedAt)
                        .ThenByDescending(r => r.Id)
                        .ToList();
                    return new BoardColumnDto { Status = status, Items = items, TotalCount = items.Count };
                }).ToList()
            };
        }
    }

    public class PagedRequestsDto
    {
        public List<FeatureRequestDto> Items { get; set; } = new();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
        public int TotalPages => PageSize <= 0 ? 0 : (TotalCount + PageSize - 1) / PageSize;
    }

    public class RequestPermissionsDto
    {
        public bool IsAuthor { get; set; }
        public bool IsOpen { get; set; }
        public bool CanLike { get; set; }
        public bool CanComment { get; set; }
        public bool CanAddInformation { get; set; }
        public bool CanChangeStatus { get; set; }
        public bool CanDelete { get; set; }
    }

    public class RequestDetailsDto
    {
        public FeatureRequestDto Request { get; set; } = new();
        public List<FeatureAdditionDto> Additions { get; set; } = new();
        public List<FeatureCommentDto> Comments { get; set; } = new();
        public List<FeatureStatusChangeDto> History { get; set; } = new();
        public bool LikedByCurrentUser { get; set; }
        public RequestPermissionsDto Permissions { get; set; } = new();
    }

    // ---- Page models ----

    /// <summary>One card on the board or a column page.</summary>
    public sealed record RequestCardModel(FeatureRequestDto Request, bool Liked, int CurrentUserId, bool OnBoard)
    {
        /// <summary>
        /// The quick like button shows on the board only (which knows what the
        /// member has liked), while the request is open, and never on their own.
        /// The API enforces the same rules.
        /// </summary>
        public bool CanLike => OnBoard && Request.Status == FeatureStatus.Requested && Request.AuthorUserId != CurrentUserId;
    }

    public static class Teaser
    {
        private static readonly System.Text.RegularExpressions.Regex Tags =
            new(@"\[/?[a-zA-Z*]+(=[^\]]*)?\]", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>The description as plain text (BBCode tags removed), cut to <paramref name="length"/> characters.</summary>
        public static string Of(string bbcode, int length = 140)
        {
            var text = System.Text.RegularExpressions.Regex.Replace(Tags.Replace(bbcode, string.Empty), @"\s+", " ").Trim();
            return text.Length <= length ? text : text[..length].TrimEnd() + "…";
        }
    }

    public class BoardPageModel
    {
        public BoardDto Board { get; set; } = new();
        /// <summary>Showing only the member's own requests.</summary>
        public bool Mine { get; set; }
        public int CurrentUserId { get; set; }
        public int PerColumn { get; set; }
    }

    public class ColumnPageModel
    {
        public FeatureStatus Status { get; set; }
        public PagedRequestsDto Results { get; set; } = new();
    }

    // ---- Forms ----

    public class RequestFormModel
    {
        [Required(ErrorMessage = "Please give the request a title.")]
        [StringLength(FeatureBoardLimits.TitleLength, ErrorMessage = "The title must be 200 characters or fewer.")]
        public string Title { get; set; } = string.Empty;

        [Required(ErrorMessage = "Please describe the feature.")]
        [StringLength(FeatureBoardLimits.DescriptionLength, ErrorMessage = "The description must be 4000 characters or fewer.")]
        public string Description { get; set; } = string.Empty;
    }
}
