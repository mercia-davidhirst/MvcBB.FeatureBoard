using System.Data;
using Dapper;
using MvcBB.API.PostgreSql;

namespace MvcBB.FeatureBoard.Api
{
    // Column names map to properties via Dapper.DefaultTypeMap.MatchNamesWithUnderscores,
    // which core's PostgreSQL provider turns on.
    public class PostgreSqlFeatureBoardRepository : IFeatureBoardRepository
    {
        // Wrapped as a derived table ("x") so the board's ordering can use the
        // computed like count.
        private const string RequestSelect = @"
            SELECT * FROM (
                SELECT r.id, r.author_user_id, u.username AS author_username, r.title, r.description,
                       r.status::int AS status, r.created_at, r.status_changed_at,
                       (SELECT COUNT(*)::int FROM public.mod_featureboard_likes l WHERE l.request_id = r.id) AS like_count,
                       (SELECT COUNT(*)::int FROM public.mod_featureboard_comments c WHERE c.request_id = r.id) AS comment_count
                FROM public.mod_featureboard_requests r
                INNER JOIN public.users u ON u.id = r.author_user_id
            ) x";

        // Requested: most liked first, then newest. Every other column: latest move first.
        private const string BoardOrder = @"
            CASE WHEN x.status = 1 THEN x.like_count ELSE 0 END DESC,
            CASE WHEN x.status = 1 THEN x.created_at ELSE x.status_changed_at END DESC,
            x.id DESC";

        private const string CommentSelect = @"
            SELECT c.id, c.request_id, c.user_id, u.username, c.content, c.created_at
            FROM public.mod_featureboard_comments c
            INNER JOIN public.users u ON u.id = c.user_id";

        private const string HistorySelect = @"
            SELECT s.id, s.request_id, s.from_status::int AS from_status, s.to_status::int AS to_status,
                   s.changed_by_user_id, u.username AS changed_by_username, s.comment, s.created_at
            FROM public.mod_featureboard_status_changes s
            INNER JOIN public.users u ON u.id = s.changed_by_user_id";

        private const string AdditionSelect = @"
            SELECT a.id, a.request_id, a.content, a.created_at
            FROM public.mod_featureboard_additions a";

        private const string Now = "(NOW() AT TIME ZONE 'utc')";

        private readonly IPostgreSqlConnectionFactory _connectionFactory;

        public PostgreSqlFeatureBoardRepository(IPostgreSqlConnectionFactory connectionFactory)
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
                           ROW_NUMBER() OVER (PARTITION BY x.status ORDER BY {BoardOrder}) AS row_number,
                           (COUNT(*) OVER (PARTITION BY x.status))::int AS column_total
                    FROM ({RequestSelect}) x
                ) ranked
                WHERE ranked.row_number <= @Take
                ORDER BY ranked.status, ranked.row_number", new { Take = perColumn }).ToList();

            return BoardRow.ToColumns(rows);
        }

        public PagedRequests GetByStatus(FeatureStatus status, int page, int pageSize)
        {
            using var connection = _connectionFactory.CreateConnection();
            var parameters = new { Status = (int)status, Skip = (page - 1) * pageSize, Take = pageSize };
            var items = connection.Query<FeatureRequest>(RequestSelect + $@"
                WHERE x.status = @Status
                ORDER BY {BoardOrder}
                OFFSET @Skip LIMIT @Take", parameters).ToList();
            var total = connection.ExecuteScalar<int>(
                "SELECT COUNT(*)::int FROM public.mod_featureboard_requests WHERE status = @Status", parameters);

            return new PagedRequests { Items = items, Page = page, PageSize = pageSize, TotalCount = total };
        }

        public IReadOnlyList<FeatureRequest> GetByAuthor(int authorUserId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<FeatureRequest>(RequestSelect + @"
                WHERE x.author_user_id = @AuthorUserId
                ORDER BY x.created_at DESC, x.id DESC", new { AuthorUserId = authorUserId }).ToList();
        }

        public IReadOnlyList<FeatureRequest> GetRecent(int count)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<FeatureRequest>(RequestSelect + @"
                WHERE x.status <> 0
                ORDER BY x.created_at DESC, x.id DESC
                LIMIT @Take", new { Take = count }).ToList();
        }

        public FeatureRequest? GetRequest(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.QuerySingleOrDefault<FeatureRequest>(RequestSelect + " WHERE x.id = @Id", new { Id = id });
        }

        public FeatureRequest AddRequest(FeatureRequest request)
        {
            using var connection = _connectionFactory.CreateConnection();
            var id = connection.QuerySingle<int>($@"
                INSERT INTO public.mod_featureboard_requests (author_user_id, title, description, status, created_at, status_changed_at)
                VALUES (@AuthorUserId, @Title, @Description, 1, {Now}, {Now})
                RETURNING id",
                new { request.AuthorUserId, request.Title, request.Description });
            return GetRequest(id)!;
        }

        public bool ChangeStatus(FeatureStatusChange change)
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var moved = connection.Execute($@"
                UPDATE public.mod_featureboard_requests
                SET status = @ToStatus, status_changed_at = {Now}
                WHERE id = @RequestId AND status = @FromStatus",
                new { change.RequestId, FromStatus = (short)change.FromStatus, ToStatus = (short)change.ToStatus }, transaction);
            if (moved == 0) return false;

            connection.Execute($@"
                INSERT INTO public.mod_featureboard_status_changes (request_id, from_status, to_status, changed_by_user_id, comment, created_at)
                VALUES (@RequestId, @FromStatus, @ToStatus, @ChangedByUserId, @Comment, {Now})",
                new
                {
                    change.RequestId,
                    FromStatus = (short)change.FromStatus,
                    ToStatus = (short)change.ToStatus,
                    change.ChangedByUserId,
                    change.Comment
                }, transaction);

            transaction.Commit();
            return true;
        }

        public void DeleteRequest(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Execute("DELETE FROM public.mod_featureboard_requests WHERE id = @Id", new { Id = id });
        }

        public IReadOnlyList<FeatureStatusChange> GetHistory(int requestId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<FeatureStatusChange>(HistorySelect + " WHERE s.request_id = @RequestId ORDER BY s.created_at, s.id",
                new { RequestId = requestId }).ToList();
        }

        // ---- Additions ----

        public IReadOnlyList<FeatureAddition> GetAdditions(int requestId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<FeatureAddition>(AdditionSelect + " WHERE a.request_id = @RequestId ORDER BY a.created_at, a.id",
                new { RequestId = requestId }).ToList();
        }

        public FeatureAddition? GetAddition(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.QuerySingleOrDefault<FeatureAddition>(AdditionSelect + " WHERE a.id = @Id", new { Id = id });
        }

        public FeatureAddition? AddAddition(FeatureAddition addition)
        {
            var id = WhileOpen(addition.RequestId, (connection, transaction) => connection.QuerySingle<int>($@"
                INSERT INTO public.mod_featureboard_additions (request_id, content, created_at)
                VALUES (@RequestId, @Content, {Now})
                RETURNING id", new { addition.RequestId, addition.Content }, transaction));
            return id == null ? null : GetAddition(id.Value);
        }

        public void DeleteAddition(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Execute("DELETE FROM public.mod_featureboard_additions WHERE id = @Id", new { Id = id });
        }

        // ---- Likes ----

        public bool HasLiked(int requestId, int userId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.ExecuteScalar<int>(
                "SELECT COUNT(*)::int FROM public.mod_featureboard_likes WHERE request_id = @RequestId AND user_id = @UserId",
                new { RequestId = requestId, UserId = userId }) > 0;
        }

        public IReadOnlyList<int> GetLikedIds(int userId, IEnumerable<int> requestIds)
        {
            var ids = requestIds.Distinct().ToArray();
            if (ids.Length == 0) return Array.Empty<int>();

            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<int>(
                "SELECT request_id FROM public.mod_featureboard_likes WHERE user_id = @UserId AND request_id = ANY(@Ids)",
                new { UserId = userId, Ids = ids }).ToList();
        }

        public bool SetLike(int requestId, int userId, bool liked)
        {
            var done = WhileOpen(requestId, (connection, transaction) =>
            {
                var sql = liked
                    ? $@"INSERT INTO public.mod_featureboard_likes (request_id, user_id, created_at)
                         VALUES (@RequestId, @UserId, {Now}) ON CONFLICT (request_id, user_id) DO NOTHING"
                    : "DELETE FROM public.mod_featureboard_likes WHERE request_id = @RequestId AND user_id = @UserId";
                connection.Execute(sql, new { RequestId = requestId, UserId = userId }, transaction);
                return (int?)1;
            });
            return done != null;
        }

        // ---- Comments ----

        public IReadOnlyList<FeatureComment> GetComments(int requestId)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.Query<FeatureComment>(CommentSelect + " WHERE c.request_id = @RequestId ORDER BY c.created_at, c.id",
                new { RequestId = requestId }).ToList();
        }

        public FeatureComment? GetComment(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            return connection.QuerySingleOrDefault<FeatureComment>(CommentSelect + " WHERE c.id = @Id", new { Id = id });
        }

        public FeatureComment? AddComment(FeatureComment comment)
        {
            var id = WhileOpen(comment.RequestId, (connection, transaction) => connection.QuerySingle<int>($@"
                INSERT INTO public.mod_featureboard_comments (request_id, user_id, content, created_at)
                VALUES (@RequestId, @UserId, @Content, {Now})
                RETURNING id",
                new { comment.RequestId, comment.UserId, comment.Content }, transaction));
            return id == null ? null : GetComment(id.Value);
        }

        public void DeleteComment(int id)
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Execute("DELETE FROM public.mod_featureboard_comments WHERE id = @Id", new { Id = id });
        }

        /// <summary>
        /// Runs <paramref name="write"/> only if the request is in Requested,
        /// holding a row lock on it until the write commits, so a status change
        /// (which updates that row) waits for it or runs first. Returns null if
        /// the request doesn't exist or isn't open.
        /// </summary>
        private int? WhileOpen(int requestId, Func<IDbConnection, IDbTransaction, int?> write)
        {
            using var connection = _connectionFactory.CreateConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var status = connection.QuerySingleOrDefault<int?>(
                "SELECT status::int FROM public.mod_featureboard_requests WHERE id = @Id FOR UPDATE",
                new { Id = requestId }, transaction);
            if (status != (int)FeatureStatus.Requested) return null;

            var result = write(connection, transaction);
            transaction.Commit();
            return result;
        }
    }
}
