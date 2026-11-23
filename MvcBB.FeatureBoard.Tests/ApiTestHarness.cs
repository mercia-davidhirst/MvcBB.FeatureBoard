using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MvcBB.API.InMemory;
using MvcBB.FeatureBoard.Api;
using MvcBB.Shared.Models.User;
using MvcBB.Shared.Models.WordFilter;

namespace MvcBB.FeatureBoard.Tests;

/// <summary>
/// The API controller wired to the in-memory provider, with three members:
/// a request's author, another member, and an Administrator. The clock moves
/// forward a second on every write, so ordering by time is deterministic.
/// </summary>
internal sealed class ApiTestHarness
{
    public const int AuthorId = 10;
    public const int MemberId = 11;
    public const int AdminId = 12;

    private DateTime _now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public InMemoryStore CoreStore { get; } = new();
    public IFeatureBoardRepository Repository { get; }

    public ApiTestHarness()
    {
        CoreStore.Users.Add(new User { Id = AuthorId, Username = "Ideasmith", Role = UserRole.User });
        CoreStore.Users.Add(new User { Id = MemberId, Username = "Upvoter", Role = UserRole.User });
        CoreStore.Users.Add(new User { Id = AdminId, Username = "Keeper", Role = UserRole.Administrator });
        Repository = new InMemoryFeatureBoardRepository(new FeatureBoardInMemoryStore(), CoreStore, () => _now = _now.AddSeconds(1));
    }

    public void AddWordFilter(string word, string replacement) =>
        CoreStore.WordFilters.Add(new WordFilterModel
        {
            Id = CoreStore.WordFilters.Count + 1,
            Word = word,
            Replacement = replacement,
            IsActive = true
        });

    public FeatureBoardController As(int userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (userId == AdminId) claims.Add(new Claim(ClaimTypes.Role, "Administrator"));

        return new FeatureBoardController(Repository, new InMemoryWordFilterRepository(CoreStore))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
            }
        };
    }

    // ---- Shortcuts that go through the controller, as a member would ----

    public FeatureRequest CreateRequest(string title = "Dark mode", string description = "Please add a dark theme.", int authorId = AuthorId) =>
        Value(As(authorId).CreateRequest(new CreateRequestRequest { Title = title, Description = description }));

    public IActionResult Move(int requestId, FeatureStatus status, string? comment = "Because.", int userId = AdminId) =>
        As(userId).ChangeStatus(requestId, new ChangeStatusRequest { Status = status, Comment = comment });

    public static T Value<T>(ActionResult<T> result) where T : class =>
        result.Value ?? (T)((ObjectResult)result.Result!).Value!;

    public static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException("Unexpected result " + result.GetType().Name)
    };

    public static int StatusOf<T>(ActionResult<T> result) =>
        result.Result == null ? 200 : StatusOf(result.Result);
}
