namespace MvcBB.FeatureBoard.Api
{
    /// <summary>
    /// Who may do what. Pure rules, no data access, so every endpoint applies
    /// them the same way (and they can be tested on their own).
    ///
    /// - Every signed-in member can see the board and every request, and can
    ///   make a request.
    /// - A request is "open" only while it's in Requested. Only then can members
    ///   like it (never their own), comment on it, and can its author add more
    ///   information. Once it moves on, likes, comments and additions are
    ///   frozen as they were (and open again if it's ever moved back).
    /// - Only Administrators move a request between columns.
    /// - The author can delete their own request, comments or additions while
    ///   the request is open; Administrators can delete them at any time.
    /// </summary>
    public static class FeatureBoardAccess
    {
        public static bool IsAuthor(FeatureRequest request, int viewerId) => request.AuthorUserId == viewerId;

        public static bool IsOpen(FeatureRequest request) => request.Status == FeatureStatus.Requested;

        public static bool CanLike(FeatureRequest request, int viewerId) => IsOpen(request) && !IsAuthor(request, viewerId);

        public static bool CanComment(FeatureRequest request) => IsOpen(request);

        public static bool CanAddInformation(FeatureRequest request, int viewerId) => IsOpen(request) && IsAuthor(request, viewerId);

        public static bool CanChangeStatus(bool isAdmin) => isAdmin;

        public static bool CanDelete(FeatureRequest request, int viewerId, bool isAdmin) =>
            isAdmin || (IsOpen(request) && IsAuthor(request, viewerId));

        public static bool CanDeleteComment(FeatureRequest request, FeatureComment comment, int viewerId, bool isAdmin) =>
            isAdmin || (IsOpen(request) && comment.UserId == viewerId);

        public static bool CanDeleteAddition(FeatureRequest request, int viewerId, bool isAdmin) =>
            isAdmin || (IsOpen(request) && IsAuthor(request, viewerId));

        /// <summary>
        /// Checks a status change's target and comment. Returns the message to
        /// show, or null when it's fine. A reason is required for every move
        /// except discarding.
        /// </summary>
        public static string? ValidateStatusChange(FeatureRequest request, FeatureStatus newStatus, string comment)
        {
            if (!FeatureStatuses.IsDefined(newStatus)) return "Unknown status.";
            if (newStatus == request.Status) return "The request is already in that column.";
            if (newStatus != FeatureStatus.Discarded && comment.Trim().Length == 0)
                return "Please give a comment explaining the change.";
            return null;
        }

        public static RequestPermissions For(FeatureRequest request, int viewerId, bool isAdmin) => new()
        {
            IsAuthor = IsAuthor(request, viewerId),
            IsOpen = IsOpen(request),
            CanLike = CanLike(request, viewerId),
            CanComment = CanComment(request),
            CanAddInformation = CanAddInformation(request, viewerId),
            CanChangeStatus = CanChangeStatus(isAdmin),
            CanDelete = CanDelete(request, viewerId, isAdmin)
        };
    }
}
