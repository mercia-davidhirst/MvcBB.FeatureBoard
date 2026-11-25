namespace MvcBB.FeatureBoard.App
{
    /// <summary>
    /// Client for MvcBB.FeatureBoard.Api. Every call carries the signed-in
    /// member's own token. Failures the member should see come back as
    /// <see cref="FeatureBoardServiceException"/>; reads of something that
    /// doesn't exist return null.
    /// </summary>
    public interface IFeatureBoardService
    {
        Task<BoardDto> GetBoardAsync(int perColumn);
        Task<PagedRequestsDto> GetColumnAsync(FeatureStatus status, int page, int pageSize);

        /// <summary>How many requests are waiting in Requested (for the Administrators' new-request alert).</summary>
        Task<int> GetRequestedCountAsync();

        /// <summary>
        /// The newest requests in any column but Discarded, newest first (for the home page
        /// card). Null if the API is older than the App and doesn't offer them.
        /// </summary>
        Task<IReadOnlyList<FeatureRequestDto>?> GetRecentRequestsAsync(int count);

        Task<IReadOnlyList<FeatureRequestDto>> GetMyRequestsAsync();
        Task<RequestDetailsDto?> GetRequestAsync(int id);
        Task<FeatureRequestDto> CreateRequestAsync(string title, string description);
        Task ChangeStatusAsync(int id, FeatureStatus status, string? comment);
        Task DeleteRequestAsync(int id);

        Task AddInformationAsync(int requestId, string content);

        /// <summary>Returns the id of the request the information was on.</summary>
        Task<int> DeleteInformationAsync(int id);

        Task SetLikeAsync(int requestId, bool liked);
        Task AddCommentAsync(int requestId, string content);

        /// <summary>Returns the id of the request the comment was on.</summary>
        Task<int> DeleteCommentAsync(int id);

        /// <summary>Core's active smilies (code to image markup), cached briefly. Empty if they can't be loaded.</summary>
        Task<Dictionary<string, string>> GetSmiliesAsync();
    }
}
