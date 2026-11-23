namespace MvcBB.FeatureBoard.App
{
    /// <summary>User-facing error for this mod - mirrors MvcBB.App's own ServiceException,
    /// which this mod can't reference directly (mods only depend on MvcBB.ModKit and MvcBB.Shared).</summary>
    public class FeatureBoardServiceException : Exception
    {
        public FeatureBoardServiceException(string message, bool notFound = false) : base(message)
        {
            NotFound = notFound;
        }

        /// <summary>The API said the request (or comment, or addition) doesn't exist.</summary>
        public bool NotFound { get; }
    }
}
