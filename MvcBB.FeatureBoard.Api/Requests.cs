using System.ComponentModel.DataAnnotations;

namespace MvcBB.FeatureBoard.Api
{
    /// <summary>Size limits shared by the API's validation and the database columns.</summary>
    public static class FeatureBoardLimits
    {
        public const int TitleLength = 200;
        public const int DescriptionLength = 4000;
        public const int AdditionLength = 4000;
        public const int CommentLength = 2000;
        public const int StatusCommentLength = 2000;
    }

    public class CreateRequestRequest
    {
        [Required, StringLength(FeatureBoardLimits.TitleLength, MinimumLength = 1)]
        public string Title { get; set; } = string.Empty;

        [Required, StringLength(FeatureBoardLimits.DescriptionLength, MinimumLength = 1)]
        public string Description { get; set; } = string.Empty;
    }

    public class AddAdditionRequest
    {
        [Required, StringLength(FeatureBoardLimits.AdditionLength, MinimumLength = 1)]
        public string Content { get; set; } = string.Empty;
    }

    public class AddCommentRequest
    {
        [Required, StringLength(FeatureBoardLimits.CommentLength, MinimumLength = 1)]
        public string Content { get; set; } = string.Empty;
    }

    public class SetLikeRequest
    {
        public bool Liked { get; set; }
    }

    public class ChangeStatusRequest
    {
        /// <summary>The column to move to. Required: a missing value must never mean Discarded (0).</summary>
        [Required]
        public FeatureStatus? Status { get; set; }

        /// <summary>The reason for the move. Required, except when discarding.</summary>
        [StringLength(FeatureBoardLimits.StatusCommentLength)]
        public string? Comment { get; set; }
    }
}
