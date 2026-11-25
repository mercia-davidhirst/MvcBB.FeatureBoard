using MvcBB.FeatureBoard.Api;
using static MvcBB.FeatureBoard.Tests.ApiTestHarness;

namespace MvcBB.FeatureBoard.Tests;

public class FeatureBoardControllerTests
{
    // ---- Creating and the board ----

    [Fact]
    public void NewRequest_StartsInRequested_AndShowsOnTheBoard()
    {
        var h = new ApiTestHarness();
        var request = h.CreateRequest();

        Assert.Equal(FeatureStatus.Requested, request.Status);
        Assert.Equal("Ideasmith", request.AuthorUsername);

        var board = Value(h.As(MemberId).GetBoard());
        Assert.Equal(FeatureStatuses.BoardOrder, board.Columns.Select(c => c.Status));
        var requested = board.Columns.Single(c => c.Status == FeatureStatus.Requested);
        Assert.Equal(new[] { request.Id }, requested.Items.Select(r => r.Id));
        Assert.All(board.Columns.Where(c => c.Status != FeatureStatus.Requested), c => Assert.Equal(0, c.TotalCount));
    }

    [Fact]
    public void CreateRequest_RejectsBlankText_AndAppliesTheWordFilter()
    {
        var h = new ApiTestHarness();
        h.AddWordFilter("darn", "****");

        Assert.Equal(400, StatusOf(h.As(AuthorId).CreateRequest(new CreateRequestRequest { Title = "   ", Description = "x" })));
        Assert.Equal(400, StatusOf(h.As(AuthorId).CreateRequest(new CreateRequestRequest { Title = "x", Description = " " })));

        var request = h.CreateRequest("A darn good idea", "Darn useful.");
        Assert.Equal("A **** good idea", request.Title);
        Assert.DoesNotContain("darn", request.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Board_SortsRequestedByLikes_AndOtherColumnsByLatestMove()
    {
        var h = new ApiTestHarness();
        var quiet = h.CreateRequest("Quiet");
        var popular = h.CreateRequest("Popular");
        var newest = h.CreateRequest("Newest");
        h.As(MemberId).SetLike(popular.Id, new SetLikeRequest { Liked = true });
        h.As(AdminId).SetLike(popular.Id, new SetLikeRequest { Liked = true });
        h.As(MemberId).SetLike(quiet.Id, new SetLikeRequest { Liked = true });

        var requested = Value(h.As(MemberId).GetBoard()).Columns.Single(c => c.Status == FeatureStatus.Requested);
        Assert.Equal(new[] { popular.Id, quiet.Id, newest.Id }, requested.Items.Select(r => r.Id));

        h.Move(quiet.Id, FeatureStatus.Todo);
        h.Move(newest.Id, FeatureStatus.Todo);
        var todo = Value(h.As(MemberId).GetBoard()).Columns.Single(c => c.Status == FeatureStatus.Todo);
        Assert.Equal(new[] { newest.Id, quiet.Id }, todo.Items.Select(r => r.Id));
    }

    [Fact]
    public void Board_LimitsEachColumn_ButCountsThemAll_AndPagesAColumn()
    {
        var h = new ApiTestHarness();
        for (var i = 0; i < 5; i++) h.CreateRequest("Idea " + i);

        var column = Value(h.As(MemberId).GetBoard(perColumn: 2)).Columns.Single(c => c.Status == FeatureStatus.Requested);
        Assert.Equal(2, column.Items.Count);
        Assert.Equal(5, column.TotalCount);

        var page3 = Value(h.As(MemberId).GetByStatus(FeatureStatus.Requested, page: 3, pageSize: 2));
        Assert.Single(page3.Items);
        Assert.Equal(5, page3.TotalCount);

        Assert.Equal(400, StatusOf(h.As(MemberId).GetByStatus((FeatureStatus)42)));
    }

    [Fact]
    public void Board_ReportsWhatTheViewerLiked()
    {
        var h = new ApiTestHarness();
        var liked = h.CreateRequest("Liked");
        h.CreateRequest("Not liked");
        h.As(MemberId).SetLike(liked.Id, new SetLikeRequest { Liked = true });

        Assert.Equal(new[] { liked.Id }, Value(h.As(MemberId).GetBoard()).LikedIds);
        Assert.Empty(Value(h.As(AdminId).GetBoard()).LikedIds);
    }

    // ---- Status changes ----

    [Fact]
    public void OnlyAdministrators_CanChangeStatus()
    {
        var h = new ApiTestHarness();
        var request = h.CreateRequest();

        Assert.Equal(403, StatusOf(h.Move(request.Id, FeatureStatus.Todo, userId: AuthorId)));
        Assert.Equal(403, StatusOf(h.Move(request.Id, FeatureStatus.Todo, userId: MemberId)));
        Assert.Equal(FeatureStatus.Requested, h.Repository.GetRequest(request.Id)!.Status);

        Assert.Equal(200, StatusOf(h.Move(request.Id, FeatureStatus.Todo, "Accepted for the next release.")));
        Assert.Equal(FeatureStatus.Todo, h.Repository.GetRequest(request.Id)!.Status);
    }

    [Fact]
    public void StatusChange_NeedsAComment_ExceptWhenDiscarding()
    {
        var h = new ApiTestHarness();
        var request = h.CreateRequest();

        Assert.Equal(400, StatusOf(h.Move(request.Id, FeatureStatus.Todo, null)));
        Assert.Equal(400, StatusOf(h.Move(request.Id, FeatureStatus.InProgress, "   ")));
        Assert.Equal(FeatureStatus.Requested, h.Repository.GetRequest(request.Id)!.Status);

        Assert.Equal(200, StatusOf(h.Move(request.Id, FeatureStatus.Discarded, null)));
        Assert.Equal(FeatureStatus.Discarded, h.Repository.GetRequest(request.Id)!.Status);
    }

    [Fact]
    public void StatusChange_RejectsTheSameColumnAndUnknownStatuses()
    {
        var h = new ApiTestHarness();
        var request = h.CreateRequest();

        Assert.Equal(400, StatusOf(h.Move(request.Id, FeatureStatus.Requested)));
        Assert.Equal(400, StatusOf(h.Move(request.Id, (FeatureStatus)9)));
        Assert.Equal(404, StatusOf(h.Move(999, FeatureStatus.Todo)));

        // A missing status must never fall back to Discarded (0).
        Assert.Equal(400, StatusOf(h.As(AdminId).ChangeStatus(request.Id, new ChangeStatusRequest { Status = null, Comment = "x" })));
        Assert.Equal(FeatureStatus.Requested, h.Repository.GetRequest(request.Id)!.Status);
    }

    [Fact]
    public void StatusChanges_AreRecordedInOrder_WithTheAdministratorsComment()
    {
        var h = new ApiTestHarness();
        h.AddWordFilter("darn", "****");
        var request = h.CreateRequest();

        h.Move(request.Id, FeatureStatus.Todo, "Planned.");
        h.Move(request.Id, FeatureStatus.InProgress, "Started, darn it.");
        h.Move(request.Id, FeatureStatus.Released, "Out now!");

        var history = Value(h.As(MemberId).GetRequest(request.Id)).History;
        Assert.Equal(new[] { FeatureStatus.Todo, FeatureStatus.InProgress, FeatureStatus.Released }, history.Select(c => c.ToStatus));
        Assert.Equal(new[] { FeatureStatus.Requested, FeatureStatus.Todo, FeatureStatus.InProgress }, history.Select(c => c.FromStatus));
        Assert.All(history, c => Assert.Equal("Keeper", c.ChangedByUsername));
        Assert.Equal("Started, **** it.", history[1].Comment);
    }

    // ---- Open only while Requested ----

    [Theory]
    [InlineData(FeatureStatus.Discarded)]
    [InlineData(FeatureStatus.Todo)]
    [InlineData(FeatureStatus.InProgress)]
    [InlineData(FeatureStatus.Released)]
    public void LikesCommentsAndAdditions_AreRefused_OnceARequestMovesOn(FeatureStatus status)
    {
        var h = new ApiTestHarness();
        var request = h.CreateRequest();
        h.As(MemberId).SetLike(request.Id, new SetLikeRequest { Liked = true });
        h.Move(request.Id, status);

        Assert.Equal(409, StatusOf(h.As(AdminId).SetLike(request.Id, new SetLikeRequest { Liked = true })));
        Assert.Equal(409, StatusOf(h.As(MemberId).SetLike(request.Id, new SetLikeRequest { Liked = false })));
        Assert.Equal(409, StatusOf(h.As(MemberId).AddComment(request.Id, new AddCommentRequest { Content = "Too late?" })));
        Assert.Equal(409, StatusOf(h.As(AuthorId).AddAddition(request.Id, new AddAdditionRequest { Content = "One more thing" })));

        var stored = h.Repository.GetRequest(request.Id)!;
        Assert.Equal(1, stored.LikeCount);
        Assert.Equal(0, stored.CommentCount);
        Assert.Empty(h.Repository.GetAdditions(request.Id));

        var permissions = Value(h.As(MemberId).GetRequest(request.Id)).Permissions;
        Assert.False(permissions.IsOpen);
        Assert.False(permissions.CanLike);
        Assert.False(permissions.CanComment);
        Assert.False(Value(h.As(AuthorId).GetRequest(request.Id)).Permissions.CanAddInformation);
    }

    [Fact]
    public void MovingBackToRequested_OpensItAgain()
    {
        var h = new ApiTestHarness();
        var request = h.CreateRequest();
        h.Move(request.Id, FeatureStatus.Discarded, null);
        h.Move(request.Id, FeatureStatus.Requested, "Reconsidering - tell us more.");

        Assert.Equal(200, StatusOf(h.As(MemberId).AddComment(request.Id, new AddCommentRequest { Content = "Yes please" })));
        Assert.Equal(200, StatusOf(h.As(MemberId).SetLike(request.Id, new SetLikeRequest { Liked = true })));
    }

    [Fact]
    public void RepositoryWrites_CheckTheStatusThemselves()
    {
        // The controller checks first, but the repository re-checks in the same
        // transaction as the write, so a status change racing with a like
        // can't let it slip through.
        var h = new ApiTestHarness();
        var request = h.CreateRequest();
        h.Move(request.Id, FeatureStatus.Todo);

        Assert.False(h.Repository.SetLike(request.Id, MemberId, true));
        Assert.Null(h.Repository.AddComment(new FeatureComment { RequestId = request.Id, UserId = MemberId, Content = "x" }));
        Assert.Null(h.Repository.AddAddition(new FeatureAddition { RequestId = request.Id, Content = "x" }));
        Assert.False(h.Repository.ChangeStatus(new FeatureStatusChange
        {
            RequestId = request.Id, FromStatus = FeatureStatus.Requested, ToStatus = FeatureStatus.Released, ChangedByUserId = AdminId
        }));
    }

    // ---- Likes ----

    [Fact]
    public void Members_CanLikeAndUnlike_ButNotTheirOwnRequest()
    {
        var h = new ApiTestHarness();
        var request = h.CreateRequest();

        Assert.Equal(400, StatusOf(h.As(AuthorId).SetLike(request.Id, new SetLikeRequest { Liked = true })));
        Assert.False(Value(h.As(AuthorId).GetRequest(request.Id)).Permissions.CanLike);

        Assert.Equal(200, StatusOf(h.As(MemberId).SetLike(request.Id, new SetLikeRequest { Liked = true })));
        Assert.Equal(200, StatusOf(h.As(MemberId).SetLike(request.Id, new SetLikeRequest { Liked = true })));
        Assert.Equal(1, h.Repository.GetRequest(request.Id)!.LikeCount);
        Assert.True(Value(h.As(MemberId).GetRequest(request.Id)).LikedByCurrentUser);

        h.As(MemberId).SetLike(request.Id, new SetLikeRequest { Liked = false });
        Assert.Equal(0, h.Repository.GetRequest(request.Id)!.LikeCount);
        Assert.Equal(404, StatusOf(h.As(MemberId).SetLike(999, new SetLikeRequest { Liked = true })));
    }

    // ---- Additional information ----

    [Fact]
    public void OnlyTheAuthor_CanAddInformation()
    {
        var h = new ApiTestHarness();
        var request = h.CreateRequest();

        Assert.Equal(403, StatusOf(h.As(MemberId).AddAddition(request.Id, new AddAdditionRequest { Content = "Me too" })));
        Assert.Equal(403, StatusOf(h.As(AdminId).AddAddition(request.Id, new AddAdditionRequest { Content = "Admin note" })));
        Assert.Equal(400, StatusOf(h.As(AuthorId).AddAddition(request.Id, new AddAdditionRequest { Content = "  " })));

        Assert.Equal(200, StatusOf(h.As(AuthorId).AddAddition(request.Id, new AddAdditionRequest { Content = "It should follow the OS setting." })));
        var details = Value(h.As(MemberId).GetRequest(request.Id));
        Assert.Equal("It should follow the OS setting.", Assert.Single(details.Additions).Content);
        Assert.False(details.Additions[0].CanDelete);
        Assert.True(Value(h.As(AuthorId).GetRequest(request.Id)).Additions[0].CanDelete);
    }

    [Fact]
    public void Additions_CanBeDeletedByTheAuthorWhileOpen_AndByAdministratorsAlways()
    {
        var h = new ApiTestHarness();
        var request = h.CreateRequest();
        var first = Value(h.As(AuthorId).AddAddition(request.Id, new AddAdditionRequest { Content = "One" }));
        var second = Value(h.As(AuthorId).AddAddition(request.Id, new AddAdditionRequest { Content = "Two" }));

        Assert.Equal(403, StatusOf(h.As(MemberId).DeleteAddition(first.Id)));
        Assert.Equal(200, StatusOf(h.As(AuthorId).DeleteAddition(first.Id)));

        h.Move(request.Id, FeatureStatus.Todo);
        Assert.Equal(409, StatusOf(h.As(AuthorId).DeleteAddition(second.Id)));
        Assert.Equal(200, StatusOf(h.As(AdminId).DeleteAddition(second.Id)));
        Assert.Empty(h.Repository.GetAdditions(request.Id));
    }

    // ---- Comments ----

    [Fact]
    public void Comments_AreFiltered_AndDeletableByWriterWhileOpen_OrByAdministrators()
    {
        var h = new ApiTestHarness();
        h.AddWordFilter("darn", "****");
        var request = h.CreateRequest();

        var mine = Value(h.As(MemberId).AddComment(request.Id, new AddCommentRequest { Content = "darn good idea" }));
        var theirs = Value(h.As(AuthorId).AddComment(request.Id, new AddCommentRequest { Content = "Thanks!" }));
        Assert.Equal("**** good idea", mine.Content);
        Assert.Equal(400, StatusOf(h.As(MemberId).AddComment(request.Id, new AddCommentRequest { Content = "   " })));

        Assert.Equal(403, StatusOf(h.As(MemberId).DeleteComment(theirs.Id)));

        var memberView = Value(h.As(MemberId).GetRequest(request.Id)).Comments;
        Assert.True(memberView.Single(c => c.Id == mine.Id).CanDelete);
        Assert.False(memberView.Single(c => c.Id == theirs.Id).CanDelete);

        h.Move(request.Id, FeatureStatus.Released);
        Assert.Equal(409, StatusOf(h.As(MemberId).DeleteComment(mine.Id)));
        Assert.Equal(200, StatusOf(h.As(AdminId).DeleteComment(mine.Id)));
        Assert.Equal(404, StatusOf(h.As(AdminId).DeleteComment(mine.Id)));
    }

    // ---- Deleting requests ----

    [Fact]
    public void Authors_CanDeleteWhileOpen_AdministratorsAlways()
    {
        var h = new ApiTestHarness();
        var open = h.CreateRequest("Open");
        var moved = h.CreateRequest("Moved");
        h.Move(moved.Id, FeatureStatus.Todo);

        Assert.Equal(403, StatusOf(h.As(MemberId).DeleteRequest(open.Id)));
        Assert.Equal(403, StatusOf(h.As(AuthorId).DeleteRequest(moved.Id)));
        Assert.Equal(204, StatusOf(h.As(AuthorId).DeleteRequest(open.Id)));
        Assert.Equal(204, StatusOf(h.As(AdminId).DeleteRequest(moved.Id)));
        Assert.Equal(404, StatusOf(h.As(MemberId).GetRequest(moved.Id)));
    }

    [Fact]
    public void DeletingARequest_RemovesEverythingOnIt()
    {
        var h = new ApiTestHarness();
        var request = h.CreateRequest();
        h.As(MemberId).SetLike(request.Id, new SetLikeRequest { Liked = true });
        h.As(MemberId).AddComment(request.Id, new AddCommentRequest { Content = "Nice" });
        h.As(AuthorId).AddAddition(request.Id, new AddAdditionRequest { Content = "More" });
        h.Move(request.Id, FeatureStatus.Todo);

        h.As(AdminId).DeleteRequest(request.Id);

        Assert.Empty(h.Repository.GetComments(request.Id));
        Assert.Empty(h.Repository.GetAdditions(request.Id));
        Assert.Empty(h.Repository.GetHistory(request.Id));
        Assert.False(h.Repository.HasLiked(request.Id, MemberId));
    }

    [Fact]
    public void Mine_ListsOnlyTheMembersOwnRequests()
    {
        var h = new ApiTestHarness();
        var mine = h.CreateRequest("Mine");
        h.CreateRequest("Theirs", authorId: MemberId);

        var result = Value(h.As(AuthorId).Mine());
        Assert.Equal(new[] { mine.Id }, result.Select(r => r.Id));
    }

    [Fact]
    public void Recent_ListsTheNewestRequestsInAnyColumnButDiscarded_ForEveryMember()
    {
        var h = new ApiTestHarness();
        var ids = Enumerable.Range(0, 7).Select(i => h.CreateRequest("Idea " + i, authorId: i % 2 == 0 ? AuthorId : MemberId).Id).ToList();
        h.Move(ids[6], FeatureStatus.Discarded);
        h.Move(ids[5], FeatureStatus.InProgress);

        var recent = Value(h.As(MemberId).Recent());
        Assert.Equal(new[] { ids[5], ids[4], ids[3], ids[2], ids[1] }, recent.Select(r => r.Id));
        Assert.Equal(FeatureStatus.InProgress, recent.First().Status);

        Assert.Single(Value(h.As(AuthorId).Recent(count: 0)));
        Assert.Equal(6, Value(h.As(AuthorId).Recent(count: 500)).Count());
    }
}
