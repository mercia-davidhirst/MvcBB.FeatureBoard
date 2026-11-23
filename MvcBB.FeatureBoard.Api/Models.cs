namespace MvcBB.FeatureBoard.Api
{
    /// <summary>
    /// Where a feature request is on the board. The numbers are stored in the
    /// database, so they never change; the board shows the columns in the
    /// order of <see cref="FeatureStatuses.BoardOrder"/>.
    /// </summary>
    public enum FeatureStatus
    {
        Discarded = 0,
        Requested = 1,
        Todo = 2,
        InProgress = 3,
        Released = 4
    }

    public static class FeatureStatuses
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

        public static bool IsDefined(FeatureStatus status) => Enum.IsDefined(status);
    }

    /// <summary>A member's idea for the site. Starts in <see cref="FeatureStatus.Requested"/>.</summary>
    public class FeatureRequest
    {
        public int Id { get; set; }
        public int AuthorUserId { get; set; }

        /// <summary>Joined from core's Users table on read.</summary>
        public string AuthorUsername { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        /// <summary>BBCode, rendered by core's encoding renderer.</summary>
        public string Description { get; set; } = string.Empty;

        public FeatureStatus Status { get; set; } = FeatureStatus.Requested;

        public DateTime CreatedAt { get; set; }

        /// <summary>When the status last changed (the creation time until then). Columns other than Requested sort by this.</summary>
        public DateTime StatusChangedAt { get; set; }

        // Computed on read.
        public int LikeCount { get; set; }
        public int CommentCount { get; set; }
    }

    /// <summary>Extra detail the author adds to their own request while it's still open.</summary>
    public class FeatureAddition
    {
        public int Id { get; set; }
        public int RequestId { get; set; }
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        /// <summary>Set by the API for the viewer.</summary>
        public bool CanDelete { get; set; }
    }

    public class FeatureComment
    {
        public int Id { get; set; }
        public int RequestId { get; set; }
        public int UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        /// <summary>Set by the API for the viewer.</summary>
        public bool CanDelete { get; set; }
    }

    /// <summary>One move across the board, made by an Administrator, with their comment.</summary>
    public class FeatureStatusChange
    {
        public int Id { get; set; }
        public int RequestId { get; set; }
        public FeatureStatus FromStatus { get; set; }
        public FeatureStatus ToStatus { get; set; }
        public int ChangedByUserId { get; set; }
        public string ChangedByUsername { get; set; } = string.Empty;

        /// <summary>The Administrator's reason. Empty only when a request was discarded without one.</summary>
        public string Comment { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }
    }

    public class BoardColumn
    {
        public FeatureStatus Status { get; set; }

        /// <summary>The first cards of the column (see <see cref="TotalCount"/> for how many there are in all).</summary>
        public IReadOnlyList<FeatureRequest> Items { get; set; } = Array.Empty<FeatureRequest>();

        public int TotalCount { get; set; }
    }

    public class Board
    {
        /// <summary>One column per status, in <see cref="FeatureStatuses.BoardOrder"/>.</summary>
        public IReadOnlyList<BoardColumn> Columns { get; set; } = Array.Empty<BoardColumn>();

        /// <summary>Ids of the requests the viewer has liked, among those on the board.</summary>
        public IReadOnlyList<int> LikedIds { get; set; } = Array.Empty<int>();
    }

    public class PagedRequests
    {
        public IReadOnlyList<FeatureRequest> Items { get; set; } = Array.Empty<FeatureRequest>();
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int TotalCount { get; set; }
    }

    /// <summary>What the viewer may do with a request (worked out by <see cref="FeatureBoardAccess"/>).</summary>
    public class RequestPermissions
    {
        public bool IsAuthor { get; set; }
        public bool IsOpen { get; set; }
        public bool CanLike { get; set; }
        public bool CanComment { get; set; }
        public bool CanAddInformation { get; set; }
        public bool CanChangeStatus { get; set; }
        public bool CanDelete { get; set; }
    }

    public class RequestDetails
    {
        public FeatureRequest Request { get; set; } = new();
        public IReadOnlyList<FeatureAddition> Additions { get; set; } = Array.Empty<FeatureAddition>();
        public IReadOnlyList<FeatureComment> Comments { get; set; } = Array.Empty<FeatureComment>();

        /// <summary>Oldest first.</summary>
        public IReadOnlyList<FeatureStatusChange> History { get; set; } = Array.Empty<FeatureStatusChange>();

        public bool LikedByCurrentUser { get; set; }
        public RequestPermissions Permissions { get; set; } = new();
    }
}
