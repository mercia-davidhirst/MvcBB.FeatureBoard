using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using MvcBB.Shared.Models.BBCode;

namespace MvcBB.FeatureBoard.App
{
    public class FeatureBoardService : IFeatureBoardService
    {
        private const string Base = "api/mods/featureboard/";
        private const string SmiliesCacheKey = "mods:featureboard:smilies";
        private static readonly TimeSpan SmiliesCacheDuration = TimeSpan.FromMinutes(5);

        // Enums travel as numbers; the converter also accepts names, in case
        // the API is ever configured to write them that way.
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(allowIntegerValues: true) }
        };

        private readonly HttpClient _httpClient;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly IMemoryCache _cache;
        private readonly ILogger<FeatureBoardService> _logger;

        public FeatureBoardService(IHttpClientFactory httpClientFactory, IHttpContextAccessor httpContextAccessor,
            IMemoryCache cache, ILogger<FeatureBoardService> logger)
        {
            _httpClient = httpClientFactory.CreateClient(FeatureBoardAppMod.HttpClientName);
            _httpContextAccessor = httpContextAccessor;
            _cache = cache;
            _logger = logger;
        }

        // ---- Board ----

        public async Task<BoardDto> GetBoardAsync(int perColumn) =>
            await GetAsync<BoardDto>(Base + "board?perColumn=" + perColumn.ToString(CultureInfo.InvariantCulture)) ?? new BoardDto();

        public async Task<PagedRequestsDto> GetColumnAsync(FeatureStatus status, int page, int pageSize) =>
            await GetAsync<PagedRequestsDto>(new StringBuilder(Base).Append("requests?status=")
                .Append(((int)status).ToString(CultureInfo.InvariantCulture))
                .Append("&page=").Append(page.ToString(CultureInfo.InvariantCulture))
                .Append("&pageSize=").Append(pageSize.ToString(CultureInfo.InvariantCulture)).ToString())
            ?? new PagedRequestsDto();

        public async Task<IReadOnlyList<FeatureRequestDto>> GetMyRequestsAsync() =>
            await GetAsync<List<FeatureRequestDto>>(Base + "requests/mine") ?? new List<FeatureRequestDto>();

        // ---- Requests ----

        public Task<RequestDetailsDto?> GetRequestAsync(int id) => GetAsync<RequestDetailsDto>($"{Base}requests/{id}");

        public async Task<FeatureRequestDto> CreateRequestAsync(string title, string description) =>
            (await SendAsync<FeatureRequestDto>(HttpMethod.Post, Base + "requests", new { title, description }))!;

        public Task ChangeStatusAsync(int id, FeatureStatus status, string? comment) =>
            SendAsync<JsonElement>(HttpMethod.Put, $"{Base}requests/{id}/status", new { status = (int)status, comment });

        public Task DeleteRequestAsync(int id) =>
            SendAsync<JsonElement>(HttpMethod.Delete, $"{Base}requests/{id}", null);

        // ---- Additional information ----

        public Task AddInformationAsync(int requestId, string content) =>
            SendAsync<JsonElement>(HttpMethod.Post, $"{Base}requests/{requestId}/additions", new { content });

        public async Task<int> DeleteInformationAsync(int id) =>
            ReadInt(await SendAsync<JsonElement>(HttpMethod.Delete, $"{Base}additions/{id}", null), "requestId");

        // ---- Likes and comments ----

        public Task SetLikeAsync(int requestId, bool liked) =>
            SendAsync<JsonElement>(HttpMethod.Put, $"{Base}requests/{requestId}/like", new { liked });

        public Task AddCommentAsync(int requestId, string content) =>
            SendAsync<JsonElement>(HttpMethod.Post, $"{Base}requests/{requestId}/comments", new { content });

        public async Task<int> DeleteCommentAsync(int id) =>
            ReadInt(await SendAsync<JsonElement>(HttpMethod.Delete, $"{Base}comments/{id}", null), "requestId");

        // ---- Smilies (core) ----

        public async Task<Dictionary<string, string>> GetSmiliesAsync()
        {
            if (_cache.TryGetValue(SmiliesCacheKey, out Dictionary<string, string>? cached) && cached != null)
                return cached;

            try
            {
                // Core's own read-only smilie list, the same one its post editor uses.
                var response = await _httpClient.GetAsync("api/bbcode/smilies");
                response.EnsureSuccessStatusCode();
                var smilies = await DeserializeAsync<List<SmilieModel>>(response) ?? new List<SmilieModel>();
                var result = SmilieMarkup.ToDictionary(smilies);
                _cache.Set(SmiliesCacheKey, result, SmiliesCacheDuration);
                return result;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogWarning(ex, "Feature Board: couldn't load smilies, so they show as plain text.");
                return new Dictionary<string, string>();
            }
        }

        // ---- Plumbing ----

        private async Task<T?> GetAsync<T>(string url)
        {
            Authorize();
            var response = await _httpClient.GetAsync(url);
            if (response.StatusCode == HttpStatusCode.NotFound) return default;
            await EnsureSuccessAsync(response);
            return await DeserializeAsync<T>(response);
        }

        private async Task<T?> SendAsync<T>(HttpMethod method, string url, object? payload)
        {
            Authorize();
            var request = new HttpRequestMessage(method, url);
            if (payload != null)
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _httpClient.SendAsync(request);
            await EnsureSuccessAsync(response);
            return await DeserializeAsync<T>(response);
        }

        private void Authorize()
        {
            var token = _httpContextAccessor.HttpContext?.User.FindFirst("api_token")?.Value
                ?? throw new FeatureBoardServiceException("Your session has expired. Please log in again.");
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        private static int ReadInt(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : 0;

        internal static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return default;
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }

        internal static async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new FeatureBoardServiceException("Your session has expired. Please log in again.");

            var body = await response.Content.ReadAsStringAsync();
            string? message = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                        message = m.GetString();
                    // Model validation failure: ValidationProblemDetails with an "errors" map.
                    else if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                        message = errors.EnumerateObject().SelectMany(e => e.Value.EnumerateArray()).Select(v => v.GetString()).FirstOrDefault(s => !string.IsNullOrEmpty(s));
                }
            }
            catch (JsonException)
            {
                // Not a JSON error body - fall through to the generic messages below.
            }

            throw response.StatusCode switch
            {
                HttpStatusCode.NotFound => new FeatureBoardServiceException(message ?? "That couldn't be found. It may have been deleted.", notFound: true),
                HttpStatusCode.Forbidden => new FeatureBoardServiceException(message ?? "You don't have permission to do that."),
                _ when message != null => new FeatureBoardServiceException(message),
                _ => new FeatureBoardServiceException("The Feature Board couldn't do that just now. Please try again.")
            };
        }
    }

    internal static class SmilieMarkup
    {
        /// <summary>Same shape as core's IMvcBBCodeService.GetAvailableSmiliesAsync: code to encoded image markup.</summary>
        public static Dictionary<string, string> ToDictionary(IEnumerable<SmilieModel> smilies)
        {
            var result = new Dictionary<string, string>();
            foreach (var s in smilies.Where(s => s.IsActive && !string.IsNullOrEmpty(s.Code)).OrderBy(s => s.SortOrder))
            {
                result.TryAdd(s.Code,
                    $"<img src=\"{WebUtility.HtmlEncode(s.ImagePath)}\" alt=\"{WebUtility.HtmlEncode(s.Description)}\" class=\"smilie\" />");
            }
            return result;
        }
    }
}
