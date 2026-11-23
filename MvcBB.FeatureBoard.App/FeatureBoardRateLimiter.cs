namespace MvcBB.FeatureBoard.App
{
    /// <summary>
    /// Caps how often one member can post in a short window: new requests (so
    /// nobody floods the board) and comments or additional information (so
    /// nobody floods a request). Kept in memory per App process (mods can't
    /// add the host's own rate-limiting middleware), so counts reset when
    /// MvcBB.App restarts.
    /// </summary>
    public class FeatureBoardRateLimiter
    {
        public const int MaxRequests = 5;
        public const int MaxComments = 30;
        public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

        public enum LimitedAction { Request, Comment }

        private readonly Dictionary<(LimitedAction, int), Queue<DateTime>> _attempts = new();
        private readonly object _lock = new();
        private readonly Func<DateTime> _clock;

        public FeatureBoardRateLimiter() : this(() => DateTime.UtcNow) { }

        internal FeatureBoardRateLimiter(Func<DateTime> clock)
        {
            _clock = clock;
        }

        /// <summary>Records an attempt, or returns false if the member is over the limit for that action.</summary>
        public bool TryAcquire(LimitedAction action, int userId)
        {
            var max = action == LimitedAction.Request ? MaxRequests : MaxComments;
            var now = _clock();
            lock (_lock)
            {
                var key = (action, userId);
                if (!_attempts.TryGetValue(key, out var attempts))
                {
                    attempts = new Queue<DateTime>();
                    _attempts[key] = attempts;
                }

                while (attempts.Count > 0 && now - attempts.Peek() >= Window)
                    attempts.Dequeue();

                if (attempts.Count >= max)
                    return false;

                attempts.Enqueue(now);

                // Drop members with no recent attempts so the map can't grow forever.
                if (_attempts.Count > 1000)
                {
                    foreach (var stale in _attempts.Where(kv => kv.Value.Count == 0 || now - kv.Value.Last() >= Window).Select(kv => kv.Key).ToList())
                        _attempts.Remove(stale);
                }

                return true;
            }
        }
    }
}
