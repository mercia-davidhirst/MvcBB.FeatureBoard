namespace MvcBB.FeatureBoard.Api
{
    /// <summary>
    /// Storage for the board. Writes that are only allowed while a request is
    /// open (likes, comments, additions) check the status in the same
    /// statement as the write and return false if the request has moved on,
    /// so a status change racing with them can't let one slip through.
    /// </summary>
    public interface IFeatureBoardRepository
    {
        // ---- Requests ----

        /// <summary>
        /// The first <paramref name="perColumn"/> requests of every column,
        /// with each column's total. Requested sorts by likes (most first),
        /// then newest; every other column by when it moved there, latest first.
        /// </summary>
        IReadOnlyList<BoardColumn> GetBoard(int perColumn);

        /// <summary>One column, paged, in the board's order.</summary>
        PagedRequests GetByStatus(FeatureStatus status, int page, int pageSize);

        /// <summary>Every request by this member, newest first.</summary>
        IReadOnlyList<FeatureRequest> GetByAuthor(int authorUserId);

        /// <summary>The newest <paramref name="count"/> requests in any column but Discarded, newest first.</summary>
        IReadOnlyList<FeatureRequest> GetRecent(int count);

        FeatureRequest? GetRequest(int id);

        /// <summary>Adds a request in Requested.</summary>
        FeatureRequest AddRequest(FeatureRequest request);

        /// <summary>
        /// Moves the request to <paramref name="change"/>'s ToStatus and records
        /// the change, provided it's still in FromStatus. False if someone else
        /// moved it first.
        /// </summary>
        bool ChangeStatus(FeatureStatusChange change);

        /// <summary>Deletes the request with its additions, likes, comments and history.</summary>
        void DeleteRequest(int id);

        /// <summary>Status changes, oldest first.</summary>
        IReadOnlyList<FeatureStatusChange> GetHistory(int requestId);

        // ---- Additions ----

        /// <summary>Oldest first.</summary>
        IReadOnlyList<FeatureAddition> GetAdditions(int requestId);

        FeatureAddition? GetAddition(int id);

        /// <summary>Adds it only while the request is in Requested; null otherwise.</summary>
        FeatureAddition? AddAddition(FeatureAddition addition);

        void DeleteAddition(int id);

        // ---- Likes ----

        bool HasLiked(int requestId, int userId);

        /// <summary>Of <paramref name="requestIds"/>, the ones this member has liked.</summary>
        IReadOnlyList<int> GetLikedIds(int userId, IEnumerable<int> requestIds);

        /// <summary>
        /// Adds or removes the member's like, only while the request is in
        /// Requested. Safe to repeat. False if the request isn't open.
        /// </summary>
        bool SetLike(int requestId, int userId, bool liked);

        // ---- Comments ----

        /// <summary>Oldest first.</summary>
        IReadOnlyList<FeatureComment> GetComments(int requestId);

        FeatureComment? GetComment(int id);

        /// <summary>Adds it only while the request is in Requested; null otherwise.</summary>
        FeatureComment? AddComment(FeatureComment comment);

        void DeleteComment(int id);
    }
}
