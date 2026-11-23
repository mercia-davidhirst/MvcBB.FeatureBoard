using MvcBB.API.InMemory;

namespace MvcBB.FeatureBoard.Api
{
    /// <summary>
    /// This mod's own in-process store for DataProvider=InMemory, mirroring
    /// core's InMemoryStore pattern but scoped to this mod's data. Registered
    /// as a singleton, so every access goes through <see cref="Lock"/>.
    /// </summary>
    public class FeatureBoardInMemoryStore
    {
        public object Lock { get; } = new();
        public List<FeatureRequest> Requests { get; } = new();
        public List<FeatureAddition> Additions { get; } = new();
        public List<(int RequestId, int UserId, DateTime CreatedAt)> Likes { get; } = new();
        public List<FeatureComment> Comments { get; } = new();
        public List<FeatureStatusChange> History { get; } = new();
        public int NextId;
    }

    public class InMemoryFeatureBoardRepository : IFeatureBoardRepository
    {
        private readonly FeatureBoardInMemoryStore _store;
        private readonly InMemoryStore _coreStore;
        private readonly Func<DateTime> _clock;

        public InMemoryFeatureBoardRepository(FeatureBoardInMemoryStore store, InMemoryStore coreStore)
            : this(store, coreStore, () => DateTime.UtcNow)
        {
        }

        internal InMemoryFeatureBoardRepository(FeatureBoardInMemoryStore store, InMemoryStore coreStore, Func<DateTime> clock)
        {
            _store = store;
            _coreStore = coreStore;
            _clock = clock;
        }

        private string UsernameOf(int userId) =>
            _coreStore.Users.FirstOrDefault(u => u.Id == userId)?.Username ?? string.Empty;

        private int NewId() => ++_store.NextId;

        // ---- Requests ----

        public IReadOnlyList<BoardColumn> GetBoard(int perColumn)
        {
            lock (_store.Lock)
            {
                return FeatureStatuses.BoardOrder.Select(status =>
                {
                    var all = InBoardOrder(_store.Requests.Where(r => r.Status == status).Select(CopyRequest)).ToList();
                    return new BoardColumn { Status = status, Items = all.Take(perColumn).ToList(), TotalCount = all.Count };
                }).ToList();
            }
        }

        public PagedRequests GetByStatus(FeatureStatus status, int page, int pageSize)
        {
            lock (_store.Lock)
            {
                var all = InBoardOrder(_store.Requests.Where(r => r.Status == status).Select(CopyRequest)).ToList();
                return new PagedRequests
                {
                    Items = all.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                    Page = page,
                    PageSize = pageSize,
                    TotalCount = all.Count
                };
            }
        }

        public IReadOnlyList<FeatureRequest> GetByAuthor(int authorUserId)
        {
            lock (_store.Lock)
            {
                return _store.Requests.Where(r => r.AuthorUserId == authorUserId)
                    .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                    .Select(CopyRequest).ToList();
            }
        }

        public FeatureRequest? GetRequest(int id)
        {
            lock (_store.Lock)
            {
                var request = _store.Requests.FirstOrDefault(r => r.Id == id);
                return request == null ? null : CopyRequest(request);
            }
        }

        public FeatureRequest AddRequest(FeatureRequest request)
        {
            lock (_store.Lock)
            {
                var now = _clock();
                var stored = new FeatureRequest
                {
                    Id = NewId(),
                    AuthorUserId = request.AuthorUserId,
                    Title = request.Title,
                    Description = request.Description,
                    Status = FeatureStatus.Requested,
                    CreatedAt = now,
                    StatusChangedAt = now
                };
                _store.Requests.Add(stored);
                return CopyRequest(stored);
            }
        }

        public bool ChangeStatus(FeatureStatusChange change)
        {
            lock (_store.Lock)
            {
                var request = _store.Requests.FirstOrDefault(r => r.Id == change.RequestId);
                if (request == null || request.Status != change.FromStatus) return false;

                var now = _clock();
                request.Status = change.ToStatus;
                request.StatusChangedAt = now;
                _store.History.Add(new FeatureStatusChange
                {
                    Id = NewId(),
                    RequestId = change.RequestId,
                    FromStatus = change.FromStatus,
                    ToStatus = change.ToStatus,
                    ChangedByUserId = change.ChangedByUserId,
                    Comment = change.Comment,
                    CreatedAt = now
                });
                return true;
            }
        }

        public void DeleteRequest(int id)
        {
            lock (_store.Lock)
            {
                _store.Requests.RemoveAll(r => r.Id == id);
                _store.Additions.RemoveAll(a => a.RequestId == id);
                _store.Likes.RemoveAll(l => l.RequestId == id);
                _store.Comments.RemoveAll(c => c.RequestId == id);
                _store.History.RemoveAll(h => h.RequestId == id);
            }
        }

        public IReadOnlyList<FeatureStatusChange> GetHistory(int requestId)
        {
            lock (_store.Lock)
            {
                return _store.History.Where(h => h.RequestId == requestId)
                    .OrderBy(h => h.CreatedAt).ThenBy(h => h.Id)
                    .Select(h => new FeatureStatusChange
                    {
                        Id = h.Id,
                        RequestId = h.RequestId,
                        FromStatus = h.FromStatus,
                        ToStatus = h.ToStatus,
                        ChangedByUserId = h.ChangedByUserId,
                        ChangedByUsername = UsernameOf(h.ChangedByUserId),
                        Comment = h.Comment,
                        CreatedAt = h.CreatedAt
                    }).ToList();
            }
        }

        // ---- Additions ----

        public IReadOnlyList<FeatureAddition> GetAdditions(int requestId)
        {
            lock (_store.Lock)
            {
                return _store.Additions.Where(a => a.RequestId == requestId)
                    .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
                    .Select(CopyAddition).ToList();
            }
        }

        public FeatureAddition? GetAddition(int id)
        {
            lock (_store.Lock)
            {
                var addition = _store.Additions.FirstOrDefault(a => a.Id == id);
                return addition == null ? null : CopyAddition(addition);
            }
        }

        public FeatureAddition? AddAddition(FeatureAddition addition)
        {
            lock (_store.Lock)
            {
                if (!IsOpen(addition.RequestId)) return null;
                var stored = new FeatureAddition
                {
                    Id = NewId(),
                    RequestId = addition.RequestId,
                    Content = addition.Content,
                    CreatedAt = _clock()
                };
                _store.Additions.Add(stored);
                return CopyAddition(stored);
            }
        }

        public void DeleteAddition(int id)
        {
            lock (_store.Lock)
            {
                _store.Additions.RemoveAll(a => a.Id == id);
            }
        }

        // ---- Likes ----

        public bool HasLiked(int requestId, int userId)
        {
            lock (_store.Lock)
            {
                return _store.Likes.Any(l => l.RequestId == requestId && l.UserId == userId);
            }
        }

        public IReadOnlyList<int> GetLikedIds(int userId, IEnumerable<int> requestIds)
        {
            var ids = requestIds.ToHashSet();
            lock (_store.Lock)
            {
                return _store.Likes.Where(l => l.UserId == userId && ids.Contains(l.RequestId))
                    .Select(l => l.RequestId).Distinct().ToList();
            }
        }

        public bool SetLike(int requestId, int userId, bool liked)
        {
            lock (_store.Lock)
            {
                if (!IsOpen(requestId)) return false;

                var exists = _store.Likes.Any(l => l.RequestId == requestId && l.UserId == userId);
                if (liked && !exists)
                    _store.Likes.Add((requestId, userId, _clock()));
                else if (!liked && exists)
                    _store.Likes.RemoveAll(l => l.RequestId == requestId && l.UserId == userId);
                return true;
            }
        }

        // ---- Comments ----

        public IReadOnlyList<FeatureComment> GetComments(int requestId)
        {
            lock (_store.Lock)
            {
                return _store.Comments.Where(c => c.RequestId == requestId)
                    .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                    .Select(CopyComment).ToList();
            }
        }

        public FeatureComment? GetComment(int id)
        {
            lock (_store.Lock)
            {
                var comment = _store.Comments.FirstOrDefault(c => c.Id == id);
                return comment == null ? null : CopyComment(comment);
            }
        }

        public FeatureComment? AddComment(FeatureComment comment)
        {
            lock (_store.Lock)
            {
                if (!IsOpen(comment.RequestId)) return null;
                var stored = new FeatureComment
                {
                    Id = NewId(),
                    RequestId = comment.RequestId,
                    UserId = comment.UserId,
                    Content = comment.Content,
                    CreatedAt = _clock()
                };
                _store.Comments.Add(stored);
                return CopyComment(stored);
            }
        }

        public void DeleteComment(int id)
        {
            lock (_store.Lock)
            {
                _store.Comments.RemoveAll(c => c.Id == id);
            }
        }

        // ---- Helpers (call with the lock held) ----

        private bool IsOpen(int requestId) =>
            _store.Requests.Any(r => r.Id == requestId && r.Status == FeatureStatus.Requested);

        /// <summary>The same order as the SQL providers' board query.</summary>
        private static IEnumerable<FeatureRequest> InBoardOrder(IEnumerable<FeatureRequest> requests) =>
            requests
                .OrderByDescending(r => r.Status == FeatureStatus.Requested ? r.LikeCount : 0)
                .ThenByDescending(r => r.Status == FeatureStatus.Requested ? r.CreatedAt : r.StatusChangedAt)
                .ThenByDescending(r => r.Id);

        private FeatureRequest CopyRequest(FeatureRequest r) => new()
        {
            Id = r.Id,
            AuthorUserId = r.AuthorUserId,
            AuthorUsername = UsernameOf(r.AuthorUserId),
            Title = r.Title,
            Description = r.Description,
            Status = r.Status,
            CreatedAt = r.CreatedAt,
            StatusChangedAt = r.StatusChangedAt,
            LikeCount = _store.Likes.Count(l => l.RequestId == r.Id),
            CommentCount = _store.Comments.Count(c => c.RequestId == r.Id)
        };

        private static FeatureAddition CopyAddition(FeatureAddition a) => new()
        {
            Id = a.Id,
            RequestId = a.RequestId,
            Content = a.Content,
            CreatedAt = a.CreatedAt
        };

        private FeatureComment CopyComment(FeatureComment c) => new()
        {
            Id = c.Id,
            RequestId = c.RequestId,
            UserId = c.UserId,
            Username = UsernameOf(c.UserId),
            Content = c.Content,
            CreatedAt = c.CreatedAt
        };
    }
}
