using System.Data;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MvcBB.ModKit;

namespace MvcBB.FeatureBoard.Api
{
    public class FeatureBoardMod : IApiMod
    {
        public const string ModId = "featureboard";

        public string Id => ModId;
        public string Name => "Feature Board";
        public string Version => "1.0.2";

        // Uses core's layout.head view slot, added in ModKit 1.1.0.
        public string MinCoreVersion => "1.1.0";
        public string? MaxCoreVersion => null;

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            var dataProvider = configuration["DataProvider"] ?? "InMemory";
            switch (dataProvider)
            {
                case "Sql":
                    services.AddScoped<IFeatureBoardRepository, SqlFeatureBoardRepository>();
                    break;
                case "PostgreSql":
                    services.AddScoped<IFeatureBoardRepository, PostgreSqlFeatureBoardRepository>();
                    break;
                default:
                    services.AddSingleton<FeatureBoardInMemoryStore>();
                    services.AddSingleton<IFeatureBoardRepository, InMemoryFeatureBoardRepository>();
                    break;
            }
        }

        public async Task InstallAsync(IModInstallContext context)
        {
            switch (context.DataProvider)
            {
                case "Sql":
                    await InstallSqlServerAsync(context.GetConnection()
                        ?? throw new InvalidOperationException("Expected a SQL Server connection."));
                    break;
                case "PostgreSql":
                    await InstallPostgreSqlAsync(context.GetConnection()
                        ?? throw new InvalidOperationException("Expected a PostgreSQL connection."));
                    break;
                default:
                    // InMemory has no database to install into - FeatureBoardInMemoryStore starts empty.
                    break;
            }
        }

        // Author, commenter and Administrator columns reference core's Users
        // table without a cascade: core anonymizes accounts rather than
        // deleting them, so a member's requests and comments stay put.
        // Everything belonging to a request cascades from the request row, so
        // deleting one is a single statement.
        //
        // Status: 0 Discarded, 1 Requested, 2 To-do, 3 In progress, 4 Released
        // (FeatureStatus). Stored as a small number, never renumbered.

        internal const string SqlServerScript = @"
IF OBJECT_ID('dbo.Mod_FeatureBoard_Requests') IS NULL
BEGIN
    CREATE TABLE dbo.Mod_FeatureBoard_Requests
    (
        Id              INT IDENTITY(1,1) NOT NULL,
        AuthorUserId    INT                NOT NULL,
        Title           NVARCHAR(200)      NOT NULL,
        Description     NVARCHAR(4000)     NOT NULL,
        Status          TINYINT            NOT NULL CONSTRAINT DF_Mod_FeatureBoard_Requests_Status DEFAULT (1),
        CreatedAt       DATETIME2(3)       NOT NULL CONSTRAINT DF_Mod_FeatureBoard_Requests_CreatedAt DEFAULT (SYSUTCDATETIME()),
        StatusChangedAt DATETIME2(3)       NOT NULL CONSTRAINT DF_Mod_FeatureBoard_Requests_StatusChangedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Mod_FeatureBoard_Requests PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_Mod_FeatureBoard_Requests_Author FOREIGN KEY (AuthorUserId) REFERENCES dbo.Users (Id),
        CONSTRAINT CK_Mod_FeatureBoard_Requests_Status CHECK (Status BETWEEN 0 AND 4)
    );

    CREATE NONCLUSTERED INDEX IX_Mod_FeatureBoard_Requests_Status_StatusChangedAt ON dbo.Mod_FeatureBoard_Requests (Status, StatusChangedAt DESC);
    CREATE NONCLUSTERED INDEX IX_Mod_FeatureBoard_Requests_AuthorUserId ON dbo.Mod_FeatureBoard_Requests (AuthorUserId);
END;

IF OBJECT_ID('dbo.Mod_FeatureBoard_Additions') IS NULL
BEGIN
    CREATE TABLE dbo.Mod_FeatureBoard_Additions
    (
        Id        INT IDENTITY(1,1) NOT NULL,
        RequestId INT                NOT NULL,
        Content   NVARCHAR(4000)     NOT NULL,
        CreatedAt DATETIME2(3)       NOT NULL CONSTRAINT DF_Mod_FeatureBoard_Additions_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Mod_FeatureBoard_Additions PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_Mod_FeatureBoard_Additions_Request FOREIGN KEY (RequestId) REFERENCES dbo.Mod_FeatureBoard_Requests (Id) ON DELETE CASCADE
    );

    CREATE NONCLUSTERED INDEX IX_Mod_FeatureBoard_Additions_RequestId ON dbo.Mod_FeatureBoard_Additions (RequestId, CreatedAt);
END;

IF OBJECT_ID('dbo.Mod_FeatureBoard_Likes') IS NULL
BEGIN
    CREATE TABLE dbo.Mod_FeatureBoard_Likes
    (
        RequestId INT          NOT NULL,
        UserId    INT          NOT NULL,
        CreatedAt DATETIME2(3) NOT NULL CONSTRAINT DF_Mod_FeatureBoard_Likes_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Mod_FeatureBoard_Likes PRIMARY KEY CLUSTERED (RequestId, UserId),
        CONSTRAINT FK_Mod_FeatureBoard_Likes_Request FOREIGN KEY (RequestId) REFERENCES dbo.Mod_FeatureBoard_Requests (Id) ON DELETE CASCADE,
        CONSTRAINT FK_Mod_FeatureBoard_Likes_User FOREIGN KEY (UserId) REFERENCES dbo.Users (Id)
    );

    CREATE NONCLUSTERED INDEX IX_Mod_FeatureBoard_Likes_UserId ON dbo.Mod_FeatureBoard_Likes (UserId);
END;

IF OBJECT_ID('dbo.Mod_FeatureBoard_Comments') IS NULL
BEGIN
    CREATE TABLE dbo.Mod_FeatureBoard_Comments
    (
        Id        INT IDENTITY(1,1) NOT NULL,
        RequestId INT                NOT NULL,
        UserId    INT                NOT NULL,
        Content   NVARCHAR(2000)     NOT NULL,
        CreatedAt DATETIME2(3)       NOT NULL CONSTRAINT DF_Mod_FeatureBoard_Comments_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Mod_FeatureBoard_Comments PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_Mod_FeatureBoard_Comments_Request FOREIGN KEY (RequestId) REFERENCES dbo.Mod_FeatureBoard_Requests (Id) ON DELETE CASCADE,
        CONSTRAINT FK_Mod_FeatureBoard_Comments_User FOREIGN KEY (UserId) REFERENCES dbo.Users (Id)
    );

    CREATE NONCLUSTERED INDEX IX_Mod_FeatureBoard_Comments_RequestId ON dbo.Mod_FeatureBoard_Comments (RequestId, CreatedAt);
END;

IF OBJECT_ID('dbo.Mod_FeatureBoard_StatusChanges') IS NULL
BEGIN
    CREATE TABLE dbo.Mod_FeatureBoard_StatusChanges
    (
        Id              INT IDENTITY(1,1) NOT NULL,
        RequestId       INT                NOT NULL,
        FromStatus      TINYINT            NOT NULL,
        ToStatus        TINYINT            NOT NULL,
        ChangedByUserId INT                NOT NULL,
        Comment         NVARCHAR(2000)     NOT NULL CONSTRAINT DF_Mod_FeatureBoard_StatusChanges_Comment DEFAULT (N''),
        CreatedAt       DATETIME2(3)       NOT NULL CONSTRAINT DF_Mod_FeatureBoard_StatusChanges_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Mod_FeatureBoard_StatusChanges PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT FK_Mod_FeatureBoard_StatusChanges_Request FOREIGN KEY (RequestId) REFERENCES dbo.Mod_FeatureBoard_Requests (Id) ON DELETE CASCADE,
        CONSTRAINT FK_Mod_FeatureBoard_StatusChanges_User FOREIGN KEY (ChangedByUserId) REFERENCES dbo.Users (Id),
        CONSTRAINT CK_Mod_FeatureBoard_StatusChanges_Status CHECK (FromStatus BETWEEN 0 AND 4 AND ToStatus BETWEEN 0 AND 4)
    );

    CREATE NONCLUSTERED INDEX IX_Mod_FeatureBoard_StatusChanges_RequestId ON dbo.Mod_FeatureBoard_StatusChanges (RequestId, CreatedAt);
END;";

        internal const string PostgreSqlScript = @"
CREATE TABLE IF NOT EXISTS public.mod_featureboard_requests
(
    id                INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    author_user_id    INTEGER       NOT NULL REFERENCES public.users (id),
    title             VARCHAR(200)  NOT NULL,
    description       VARCHAR(4000) NOT NULL,
    status            SMALLINT      NOT NULL DEFAULT 1 CHECK (status BETWEEN 0 AND 4),
    created_at        TIMESTAMP(3)  NOT NULL DEFAULT (NOW() AT TIME ZONE 'utc'),
    status_changed_at TIMESTAMP(3)  NOT NULL DEFAULT (NOW() AT TIME ZONE 'utc')
);

CREATE INDEX IF NOT EXISTS ix_mod_featureboard_requests_status_changed_at ON public.mod_featureboard_requests (status, status_changed_at DESC);
CREATE INDEX IF NOT EXISTS ix_mod_featureboard_requests_author_user_id ON public.mod_featureboard_requests (author_user_id);

CREATE TABLE IF NOT EXISTS public.mod_featureboard_additions
(
    id         INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_id INTEGER       NOT NULL REFERENCES public.mod_featureboard_requests (id) ON DELETE CASCADE,
    content    VARCHAR(4000) NOT NULL,
    created_at TIMESTAMP(3)  NOT NULL DEFAULT (NOW() AT TIME ZONE 'utc')
);

CREATE INDEX IF NOT EXISTS ix_mod_featureboard_additions_request_id ON public.mod_featureboard_additions (request_id, created_at);

CREATE TABLE IF NOT EXISTS public.mod_featureboard_likes
(
    request_id INTEGER      NOT NULL REFERENCES public.mod_featureboard_requests (id) ON DELETE CASCADE,
    user_id    INTEGER      NOT NULL REFERENCES public.users (id),
    created_at TIMESTAMP(3) NOT NULL DEFAULT (NOW() AT TIME ZONE 'utc'),
    PRIMARY KEY (request_id, user_id)
);

CREATE INDEX IF NOT EXISTS ix_mod_featureboard_likes_user_id ON public.mod_featureboard_likes (user_id);

CREATE TABLE IF NOT EXISTS public.mod_featureboard_comments
(
    id         INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_id INTEGER       NOT NULL REFERENCES public.mod_featureboard_requests (id) ON DELETE CASCADE,
    user_id    INTEGER       NOT NULL REFERENCES public.users (id),
    content    VARCHAR(2000) NOT NULL,
    created_at TIMESTAMP(3)  NOT NULL DEFAULT (NOW() AT TIME ZONE 'utc')
);

CREATE INDEX IF NOT EXISTS ix_mod_featureboard_comments_request_id ON public.mod_featureboard_comments (request_id, created_at);

CREATE TABLE IF NOT EXISTS public.mod_featureboard_status_changes
(
    id                 INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    request_id         INTEGER       NOT NULL REFERENCES public.mod_featureboard_requests (id) ON DELETE CASCADE,
    from_status        SMALLINT      NOT NULL CHECK (from_status BETWEEN 0 AND 4),
    to_status          SMALLINT      NOT NULL CHECK (to_status BETWEEN 0 AND 4),
    changed_by_user_id INTEGER       NOT NULL REFERENCES public.users (id),
    comment            VARCHAR(2000) NOT NULL DEFAULT '',
    created_at         TIMESTAMP(3)  NOT NULL DEFAULT (NOW() AT TIME ZONE 'utc')
);

CREATE INDEX IF NOT EXISTS ix_mod_featureboard_status_changes_request_id ON public.mod_featureboard_status_changes (request_id, created_at);";

        private static Task InstallSqlServerAsync(IDbConnection connection) => connection.ExecuteAsync(SqlServerScript);

        private static Task InstallPostgreSqlAsync(IDbConnection connection) => connection.ExecuteAsync(PostgreSqlScript);
    }
}
