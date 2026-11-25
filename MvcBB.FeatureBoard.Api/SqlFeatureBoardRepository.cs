using System.Data;
using Dapper;
using MvcBB.API.SQL;

namespace MvcBB.FeatureBoard.Api
{
    public class SqlFeatureBoardRepository : IFeatureBoardRepository
    {
        // Wrapped as a derived table ("x") so the board's ordering can use the
        // computed like count.
        private const string RequestSelect = @"
            SELECT * FROM (
                SELECT r.Id, r.AuthorUserId, u.Username AS AuthorUsername, r.Title, r.Description,
                       CAST(r.Status AS INT) AS Status, r.CreatedAt, r.StatusChangedAt,
                       (SELECT COUNT(*) FROM dbo.Mod_FeatureBoard_Likes l WHERE l.RequestId = r.Id) AS LikeCount,
                       (SELECT COUNT(*) FROM dbo.Mod_FeatureBoard_Comments c WHERE c.RequestId = r.Id) AS CommentCount
                FROM dbo.Mod_FeatureBoard_Requests r
                INNER JOIN dbo.Users u ON u.Id = r.AuthorUserId
            ) x";

        // Requested: most liked first, then newest. Every other column: latest move first.
        private const string BoardOrder = @"
            CASE WHEN x.Status = 1 THEN x.LikeCount ELSE 0 END DESC,
            CASE WHEN x.Status = 1 THEN x.CreatedAt ELSE x.StatusChangedAt END DESC,
            x.Id DESC";

        private const string CommentSelect = @"
            SELECT c.Id, c.RequestId, c.UserId, u.Username, c.Content, c.CreatedAt
            FROM dbo.Mod_FeatureBoard_Comments c
            INNER JOIN dbo.Users u ON u.Id = c.UserId";

        private const string HistorySelect = @"
            SELECT s.Id, s.RequestId, CAST(s.FromStatus AS INT) AS FromStatus, CAST(s.ToStatus AS INT) AS ToStatus,
                   s.ChangedByUserId, u.Username AS ChangedByUsername, s.Comment, s.CreatedAt
            FROM dbo.Mod_FeatureBoard_StatusChanges s
            INNER JOIN dbo.Users u ON u.Id = s.ChangedByUserId";

        private const string AdditionSelect = @"
            SELECT a.Id, a.RequestId, a.Content, a.CreatedAt
            FROM dbo.Mod_FeatureBoard_Additions a";

        private readonly ISqlConnectionFactory _connectionFactory;

        public SqlFeatureBoardRepository(ISqlConnectionFactory connectionFactory)
        {
            _connectionFactory = connectionFactory;
        }

        // ---- Requests ----

        public IReadOnlyList<BoardColumn> GetBoard(int perColumn)
        {
            using var connection = _connectionFactory.CreateConnection();
            var rows = connection.Query<BoardRow>($@"
                SELECT * FROM (
                    SELECT x.*,
                           ROW_NUMBER() OVER (PARTITION BY x.Status ORDER BY {BoardOrder}) AS RowNumber,
                           COUNT(*) OVER (PARTITION BY x.Status) AS ColumnTotal
                    FROM ({RequestSelect}) x
                ) ranked
                WHERE ranked.RowNumber <= @Take
                ORDER BY ranked.Status, ranked.RowNumber", new { Take = perColumn }).ToList();

            return BoardRow.ToColumns(rows);
        }

        public PagedRequests GetByStatus(FeatureStatus status, int page, int pageSize)
        {
            using var connection = _connectionFactory.CreateConnection();
            var parameters = new { Status = (int)status, Skip = (page - 1) * pageSize, Take = pageSize };
            var items = connection.Query<FeatureRequest>(RequestSelect + $@"
                WHERE x.Status = @Status
                ORDER BY {BoardOrder}
                OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY", parameters).ToList();
            var total = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.Mod_FeatureBoard_Requests WHERE Status = @Status", parameters);

            return new PagedRequests { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
        }

        public IReadOnlyList<FeatureRequest> GetByAuthor(int authorUserId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<FeatureRequest>(RequestSelect + @"
                WHERE x.AuthorUserId = @AuthorUserId
                ORDER BY x.CreatedAt DESC, x.Id DESC", new { AuthorUserId = authorUserId }).ToList();
        }

        public IReadOnlyList<FeatureRequest> GetRecent(int count)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<FeatureRequest>(RequestSelect + @"
                WHERE x.Status <> 0
                ORDER BY x.CreatedAt DESC, x.Id DESC
                OFFSET 0 ROWS FETCH NEXT @Take ROWS ONLY", new { Take = count }).ToList();
        }

        public FeatureRequest? GetRequest(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.QuerySingleOrDefault<FeatureRequest>(RequestSelect + " WHERE x.Id = @Id", new { Id = id });
        }

        public FeatureRequest AddRequest(FeatureRequest request)
        {
            using var connection = _connectionFactory.CreateConnection();
            var id = connection.QuerySingle<int>(@"
                INSERT INTO dbo.Mod_FeatureBoard_Requests (AuthorUserId, Title, Description, Status, CreatedAt, StatusChangedAt)
                OUTPUT inserted.Id
                VALUES (@AuthorUserId, @Title, @Description, 1, SYSUTCDATETIME(), SYSUTCDATETIME())",
                new { request.AuthorUserId, request.Title, request.Description });
            return GetRequest(id)!;
        }

        public bool ChangeStatus(FeatureStatusChange change)
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var moved = connection.Execute(@"
                UPDATE dbo.Mod_FeatureBoard_Requests
                SET Status = @ToStatus, StatusChangedAt = SYSUTCDATETIME()
                WHERE Id = @RequestId AND Status = @FromStatus",
                new { change.RequestId, FromStatus = (int)change.FromStatus, ToStatus = (int)change.ToStatus }, transaction);
            if (moved == 0) return false;

            connection.Execute(@"
                INSERT INTO dbo.Mod_FeatureBoard_StatusChanges (RequestId, FromStatus, ToStatus, ChangedByUserId, Comment, CreatedAt)
                VALUES (@RequestId, @FromStatus, @ToStatus, @ChangedByUserId, @Comment, SYSUTCDATETIME())",
                new
                {
                    change.RequestId,
                    FromStatus = (int)change.FromStatus,
                    ToStatus = (int)change.ToStatus,
                    change.ChangedByUserId,
                    change.Comment
                }, transaction);

            transaction.Commit();
            return true;
        }

        public void DeleteRequest(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Execute("DELETE FROM dbo.Mod_FeatureBoard_Requests WHERE Id = @Id", new { Id = id });
        }

        public IReadOnlyList<FeatureStatusChange> GetHistory(int requestId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<FeatureStatusChange>(HistorySelect + " WHERE s.RequestId = @RequestId ORDER BY s.CreatedAt, s.Id",
                new { RequestId = requestId }).ToList();
        }

        // ---- Additions ----

        public IReadOnlyList<FeatureAddition> GetAdditions(int requestId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<FeatureAddition>(AdditionSelect + " WHERE a.RequestId = @RequestId ORDER BY a.CreatedAt, a.Id",
                new { RequestId = requestId }).ToList();
        }

        public FeatureAddition? GetAddition(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.QuerySingleOrDefault<FeatureAddition>(AdditionSelect + " WHERE a.Id = @Id", new { Id = id });
        }

        public FeatureAddition? AddAddition(FeatureAddition addition)
        {
            var id = WhileOpen(addition.RequestId, (connection, transaction) => connection.QuerySingle<int>(@"
                INSERT INTO dbo.Mod_FeatureBoard_Additions (RequestId, Content, CreatedAt)
                OUTPUT inserted.Id
                VALUES (@RequestId, @Content, SYSUTCDATETIME())", new { addition.RequestId, addition.Content }, transaction));
            return id == null ? null : GetAddition(id.Value);
        }

        public void DeleteAddition(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Execute("DELETE FROM dbo.Mod_FeatureBoard_Additions WHERE Id = @Id", new { Id = id });
        }

        // ---- Likes ----

        public bool HasLiked(int requestId, int userId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.Mod_FeatureBoard_Likes WHERE RequestId = @RequestId AND UserId = @UserId",
                new { RequestId = requestId, UserId = userId }) > 0;
        }

        public IReadOnlyList<int> GetLikedIds(int userId, IEnumerable<int> requestIds)
        {
            var ids = requestIds.Distinct().ToList();
            if (ids.Count == 0) return Array.Empty<int>();

            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<int>(
                "SELECT RequestId FROM dbo.Mod_FeatureBoard_Likes WHERE UserId = @UserId AND RequestId IN @Ids",
                new { UserId = userId, Ids = ids }).ToList();
        }

        public bool SetLike(int requestId, int userId, bool liked)
        {
            var done = WhileOpen(requestId, (connection, transaction) =>
            {
                var sql = liked
                    ? @"IF NOT EXISTS (SELECT 1 FROM dbo.Mod_FeatureBoard_Likes WHERE RequestId = @RequestId AND UserId = @UserId)
                        INSERT INTO dbo.Mod_FeatureBoard_Likes (RequestId, UserId, CreatedAt) VALUES (@RequestId, @UserId, SYSUTCDATETIME())"
                    : "DELETE FROM dbo.Mod_FeatureBoard_Likes WHERE RequestId = @RequestId AND UserId = @UserId";
                connection.Execute(sql, new { RequestId = requestId, UserId = userId }, transaction);
                return (int?)1;
            });
            return done != null;
        }

        // ---- Comments ----

        public IReadOnlyList<FeatureComment> GetComments(int requestId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<FeatureComment>(CommentSelect + " WHERE c.RequestId = @RequestId ORDER BY c.CreatedAt, c.Id",
                new { RequestId = requestId }).ToList();
        }

        public FeatureComment? GetComment(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.QuerySingleOrDefault<FeatureComment>(CommentSelect + " WHERE c.Id = @Id", new { Id = id });
        }

        public FeatureComment? AddComment(FeatureComment comment)
        {
            var id = WhileOpen(comment.RequestId, (connection, transaction) => connection.QuerySingle<int>(@"
                INSERT INTO dbo.Mod_FeatureBoard_Comments (RequestId, UserId, Content, CreatedAt)
                OUTPUT inserted.Id
                VALUES (@RequestId, @UserId, @Content, SYSUTCDATETIME())",
                new { comment.RequestId, comment.UserId, comment.Content }, transaction));
            return id == null ? null : GetComment(id.Value);
        }

        public void DeleteComment(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Execute("DELETE FROM dbo.Mod_FeatureBoard_Comments WHERE Id = @Id", new { Id = id });
        }

        /// <summary>
        /// Runs <paramref name="write"/> only if the request is in Requested,
        /// holding an update lock on its row until the write commits, so a
        /// status change (which updates that row) waits for it or runs first.
        /// Returns null if the request doesn't exist or isn't open.
        /// </summary>
        private int? WhileOpen(int requestId, Func<IDbConnection, IDbTransaction, int?> write)
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var status = connection.QuerySingleOrDefault<int?>(
                "SELECT CAST(Status AS INT) FROM dbo.Mod_FeatureBoard_Requests WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                new { Id = requestId }, transaction);
            if (status != (int)FeatureStatus.Requested) return null;

            var result = write(connection, transaction);
            transaction.Commit();
            return result;
        }
    }

    /// <summary>A request with its place in its column, as the board query returns it.</summary>
    internal sealed class BoardRow : FeatureRequest
    {
        public long RowNumber { get; set; }
        public int ColumnTotal { get; set; }

        /// <summary>Groups the rows into the board's columns, including empty ones.</summary>
        public static IReadOnlyList<BoardColumn> ToColumns(IEnumerable<BoardRow> rows)
        {
            var byStatus = rows.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.ToList());
            return FeatureStatuses.BoardOrder.Select(status =>
            {
                var items = byStatus.TryGetValue(status, out var list) ? list : new List<BoardRow>();
                return new BoardColumn
                {
                    Status = status,
                    Items = items.OrderBy(r => r.RowNumber).Select(Plain).ToList(),
                    TotalCount = items.Count == 0 ? 0 : items[0].ColumnTotal
                };
            }).ToList();
        }

        // Serialize plain FeatureRequests, not rows with board bookkeeping.
        private static FeatureRequest Plain(BoardRow r) => new()
        {
            Id = r.Id,
            AuthorUserId = r.AuthorUserId,
            AuthorUsername = r.AuthorUsername,
            Title = r.Title,
            Description = r.Description,
            Status = r.Status,
            CreatedAt = r.CreatedAt,
            StatusChangedAt = r.StatusChangedAt,
            LikeCount = r.LikeCount,
            CommentCount = r.CommentCount
        };
    }
}
