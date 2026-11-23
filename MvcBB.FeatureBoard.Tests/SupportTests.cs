using System.Text.RegularExpressions;
using MvcBB.FeatureBoard.Api;
using MvcBB.FeatureBoard.App;
using ApiStatus = MvcBB.FeatureBoard.Api.FeatureStatus;
using AppStatus = MvcBB.FeatureBoard.App.FeatureStatus;

namespace MvcBB.FeatureBoard.Tests;

public class SupportTests
{
    [Fact]
    public void Access_OpenOnlyWhileRequested()
    {
        var open = new FeatureRequest { AuthorUserId = 1, Status = ApiStatus.Requested };
        var released = new FeatureRequest { AuthorUserId = 1, Status = ApiStatus.Released };

        Assert.True(FeatureBoardAccess.CanLike(open, 2));
        Assert.False(FeatureBoardAccess.CanLike(open, 1));
        Assert.False(FeatureBoardAccess.CanLike(released, 2));

        Assert.True(FeatureBoardAccess.CanAddInformation(open, 1));
        Assert.False(FeatureBoardAccess.CanAddInformation(open, 2));
        Assert.False(FeatureBoardAccess.CanAddInformation(released, 1));

        Assert.True(FeatureBoardAccess.CanDelete(open, 1, isAdmin: false));
        Assert.False(FeatureBoardAccess.CanDelete(released, 1, isAdmin: false));
        Assert.True(FeatureBoardAccess.CanDelete(released, 2, isAdmin: true));
        Assert.False(FeatureBoardAccess.CanChangeStatus(isAdmin: false));
    }

    [Theory]
    [InlineData(ApiStatus.Discarded, "", null)]
    [InlineData(ApiStatus.Todo, "", "Please give a comment explaining the change.")]
    [InlineData(ApiStatus.Todo, "Planned", null)]
    [InlineData(ApiStatus.Requested, "Same", "The request is already in that column.")]
    public void Access_ValidatesStatusChanges(ApiStatus target, string comment, string? expected)
    {
        var request = new FeatureRequest { Status = ApiStatus.Requested };
        Assert.Equal(expected, FeatureBoardAccess.ValidateStatusChange(request, target, comment));
    }

    [Fact]
    public void AppStatuses_MatchTheApisNumbers_AndBoardOrder()
    {
        Assert.Equal(
            Enum.GetValues<ApiStatus>().Select(s => ((int)s, s.ToString())),
            Enum.GetValues<AppStatus>().Select(s => ((int)s, s.ToString())));
        Assert.Equal(FeatureStatuses.BoardOrder.Select(s => (int)s), FeatureStatusDisplay.BoardOrder.Select(s => (int)s));
        Assert.Equal(new[] { "Discarded", "Requested", "To-do", "In progress", "Released" },
            FeatureStatusDisplay.BoardOrder.Select(FeatureStatusDisplay.Name));
    }

    [Fact]
    public void Limits_MatchBetweenTiers()
    {
        Assert.Equal(Api.FeatureBoardLimits.TitleLength, App.FeatureBoardLimits.TitleLength);
        Assert.Equal(Api.FeatureBoardLimits.DescriptionLength, App.FeatureBoardLimits.DescriptionLength);
        Assert.Equal(Api.FeatureBoardLimits.AdditionLength, App.FeatureBoardLimits.AdditionLength);
        Assert.Equal(Api.FeatureBoardLimits.CommentLength, App.FeatureBoardLimits.CommentLength);
        Assert.Equal(Api.FeatureBoardLimits.StatusCommentLength, App.FeatureBoardLimits.StatusCommentLength);
    }

    [Fact]
    public void CardLikeButton_OnlyOnTheBoard_ForOpenRequests_NotTheMembersOwn()
    {
        var open = new FeatureRequestDto { AuthorUserId = 1, Status = AppStatus.Requested };
        var moved = new FeatureRequestDto { AuthorUserId = 1, Status = AppStatus.Todo };

        Assert.True(new RequestCardModel(open, false, 2, OnBoard: true).CanLike);
        Assert.False(new RequestCardModel(open, false, 1, OnBoard: true).CanLike);
        Assert.False(new RequestCardModel(moved, false, 2, OnBoard: true).CanLike);
        Assert.False(new RequestCardModel(open, false, 2, OnBoard: false).CanLike);
    }

    [Fact]
    public void MyRequestsBoard_HasEveryColumn_InTheBoardsOrder()
    {
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var board = BoardDto.FromRequests(new[]
        {
            new FeatureRequestDto { Id = 1, Status = AppStatus.Requested, LikeCount = 1, CreatedAt = t },
            new FeatureRequestDto { Id = 2, Status = AppStatus.Requested, LikeCount = 5, CreatedAt = t },
            new FeatureRequestDto { Id = 3, Status = AppStatus.Released, StatusChangedAt = t },
            new FeatureRequestDto { Id = 4, Status = AppStatus.Released, StatusChangedAt = t.AddDays(1) }
        });

        Assert.Equal(FeatureStatusDisplay.BoardOrder, board.Columns.Select(c => c.Status));
        Assert.Equal(new[] { 2, 1 }, board.Columns.Single(c => c.Status == AppStatus.Requested).Items.Select(r => r.Id));
        Assert.Equal(new[] { 4, 3 }, board.Columns.Single(c => c.Status == AppStatus.Released).Items.Select(r => r.Id));
        Assert.Equal(0, board.Columns.Single(c => c.Status == AppStatus.Todo).TotalCount);
    }

    [Theory]
    [InlineData("[b]Bold[/b] and [url=https://example.com]a link[/url]", 140, "Bold and a link")]
    [InlineData("Line one\n\nline   two", 140, "Line one line two")]
    [InlineData("abcdefghij", 5, "abcde…")]
    public void Teaser_StripsBBCodeAndShortens(string input, int length, string expected) =>
        Assert.Equal(expected, Teaser.Of(input, length));

    [Fact]
    public void RateLimiter_CapsRequestsPerWindow_PerMember()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var limiter = new FeatureBoardRateLimiter(() => now);

        for (var i = 0; i < FeatureBoardRateLimiter.MaxRequests; i++)
            Assert.True(limiter.TryAcquire(FeatureBoardRateLimiter.LimitedAction.Request, 1));

        Assert.False(limiter.TryAcquire(FeatureBoardRateLimiter.LimitedAction.Request, 1));
        Assert.True(limiter.TryAcquire(FeatureBoardRateLimiter.LimitedAction.Request, 2));
        Assert.True(limiter.TryAcquire(FeatureBoardRateLimiter.LimitedAction.Comment, 1));

        now += FeatureBoardRateLimiter.Window;
        Assert.True(limiter.TryAcquire(FeatureBoardRateLimiter.LimitedAction.Request, 1));
    }

    [Fact]
    public async Task ServiceErrors_CarryTheApisMessage()
    {
        var conflict = new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"message\":\"This request is no longer open\"}")
        };
        var ex = await Assert.ThrowsAsync<FeatureBoardServiceException>(() => FeatureBoardService.EnsureSuccessAsync(conflict));
        Assert.Equal("This request is no longer open", ex.Message);

        var missing = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("") };
        Assert.True((await Assert.ThrowsAsync<FeatureBoardServiceException>(() => FeatureBoardService.EnsureSuccessAsync(missing))).NotFound);
    }

    [Fact]
    public async Task Service_ReadsStatusesAsNumbers()
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":3,\"status\":3,\"title\":\"x\"}")
        };
        var dto = await FeatureBoardService.DeserializeAsync<FeatureRequestDto>(response);
        Assert.Equal(AppStatus.InProgress, dto!.Status);
    }

    [Fact]
    public void InstallScripts_AreIdempotent_AndCascadeFromRequests()
    {
        var script = FeatureBoardMod.SqlServerScript;
        foreach (var table in new[] { "Requests", "Additions", "Likes", "Comments", "StatusChanges" })
            Assert.Contains($"IF OBJECT_ID('dbo.Mod_FeatureBoard_{table}') IS NULL", script);
        Assert.Equal(4, Regex.Matches(script, "ON DELETE CASCADE").Count);

        var postgres = FeatureBoardMod.PostgreSqlScript;
        Assert.Equal(5, Regex.Matches(postgres, "CREATE TABLE IF NOT EXISTS").Count);
        Assert.Equal(4, Regex.Matches(postgres, "ON DELETE CASCADE").Count);
        Assert.DoesNotMatch(@"CREATE (UNIQUE )?INDEX (?!IF NOT EXISTS)", postgres);
    }
}
