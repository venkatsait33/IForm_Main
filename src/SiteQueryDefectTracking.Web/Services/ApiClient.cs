using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using SiteQueryDefectTracking.Web.Models;

namespace SiteQueryDefectTracking.Web.Services;

public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public ApiException(string message, int statusCode = 0, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
    }
}

public sealed class ApiClient
{
    public const string AccessTokenClaim = "sqd_access_token";
    public const string RefreshTokenClaim = "sqd_refresh_token";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true) }
    };

    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly PersistentComponentState _persistentState;
    private bool _persistRegistered;

    public sealed record TokenPair(string Access, string? Refresh);

    // Process-wide per-user token cache. Bridges the prerender -> interactive-circuit
    // boundary where the request-scoped HttpContext (and its cookie claims) is unavailable.
    private static readonly ConcurrentDictionary<string, TokenPair> TokenStore = new();

    // Key under which tokens are persisted to the rendered page during prerender and
    // restored into the interactive circuit (where HttpContext is unavailable).
    private const string PersistedTokensKey = "sqd_tokens";

    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public string? UserId { get; private set; }
    public string? FullName { get; private set; }
    public string? Email { get; private set; }
    public IReadOnlyList<string> Roles { get; private set; } = Array.Empty<string>();

    public ApiClient(IHttpClientFactory factory, IHttpContextAccessor contextAccessor, AuthenticationStateProvider authStateProvider, PersistentComponentState persistentState)
    {
        _http = factory.CreateClient("api");
        _contextAccessor = contextAccessor;
        _authStateProvider = authStateProvider;
        _persistentState = persistentState;
    }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken);

    public bool HasRole(string role)
        => Roles.Any(r => string.Equals(r, role, StringComparison.OrdinalIgnoreCase));

    // Resolves the JWT tokens for authenticated calls. Tries, in order:
    // 1. in-memory tokens already set on this instance (circuit-scoped),
    // 2. the request-scoped HttpContext principal (SSR / prerender),
    // 3. the circuit AuthenticationStateProvider (interactive events),
    // 4. the per-user token cache carried over from the initial request,
    // 5. the tokens persisted into the page DOM during prerender (interactive circuit).
    private async Task EnsureAuthenticatedAsync(CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(AccessToken)) return;

        var httpUser = _contextAccessor.HttpContext?.User;
        if (httpUser?.Identity?.IsAuthenticated == true)
        {
            RestoreFromClaims(httpUser);
        }

        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            var state = await _authStateProvider.GetAuthenticationStateAsync();
            if (state.User.Identity?.IsAuthenticated == true)
            {
                RestoreFromClaims(state.User);
            }
        }

        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            var uid = UserId ?? httpUser?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrWhiteSpace(uid) && TokenStore.TryGetValue(uid, out var pair))
            {
                AccessToken = pair.Access;
                RefreshToken = pair.Refresh;
            }
        }

        if (string.IsNullOrWhiteSpace(AccessToken))
        {
            if (_persistentState.TryTakeFromJson<TokenPair>(PersistedTokensKey, out var persisted) && persisted is not null)
            {
                AccessToken = persisted.Access;
                RefreshToken = persisted.Refresh;
            }
        }
    }

    private void RestoreFromClaims(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return;
        AccessToken = user.FindFirstValue(AccessTokenClaim);
        RefreshToken = user.FindFirstValue(RefreshTokenClaim);
        UserId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        FullName = user.FindFirstValue(ClaimTypes.Name);
        Email = user.FindFirstValue(ClaimTypes.Email);
        Roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().ToList();
        CacheTokens();
    }

    private void CacheTokens()
    {
        if (string.IsNullOrWhiteSpace(UserId) || string.IsNullOrWhiteSpace(AccessToken))
        {
            Console.WriteLine($"[diag] ApiClient: CacheTokens SKIP (uid={(UserId is null ? "null" : "set")}, token={(AccessToken is null ? "null" : "set")})");
            return;
        }
        TokenStore[UserId] = new TokenPair(AccessToken, RefreshToken);
        EnsurePersistRegistered();
    }

    private void EnsurePersistRegistered()
    {
        if (_persistRegistered) return;
        _persistRegistered = true;
        try
        {
            Console.WriteLine($"[diag] ApiClient: registering persist (token len {AccessToken?.Length ?? 0})");
            _persistentState.RegisterOnPersisting(() =>
            {
                Console.WriteLine($"[diag] ApiClient: persist callback running (token len {AccessToken?.Length ?? 0})");
                if (!string.IsNullOrWhiteSpace(AccessToken))
                {
                    _persistentState.PersistAsJson(PersistedTokensKey, new TokenPair(AccessToken, RefreshToken));
                    Console.WriteLine("[diag] ApiClient: persist callback DONE");
                }
                return Task.CompletedTask;
            });
            Console.WriteLine("[diag] ApiClient: register returned OK");
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"[diag] ApiClient: register FAILED: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[diag] ApiClient: register EXCEPTION: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public ClaimsPrincipal CreatePrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, UserId ?? string.Empty),
            new(ClaimTypes.Name, FullName ?? Email ?? string.Empty),
            new(ClaimTypes.Email, Email ?? string.Empty),
            new(AccessTokenClaim, AccessToken ?? string.Empty),
            new(RefreshTokenClaim, RefreshToken ?? string.Empty),
        };
        foreach (var role in Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        return new ClaimsPrincipal(identity);
    }

    public async Task PersistPrincipalAsync(CancellationToken ct = default)
    {
        var http = _contextAccessor.HttpContext;
        if (http?.User?.Identity?.IsAuthenticated != true) return;
        await http.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            CreatePrincipal(),
            new AuthenticationProperties { IsPersistent = true });
    }

    public void ClearSession()
    {
        if (!string.IsNullOrWhiteSpace(UserId))
        {
            TokenStore.TryRemove(UserId, out _);
        }
        AccessToken = null;
        RefreshToken = null;
        UserId = null;
        FullName = null;
        Email = null;
        Roles = Array.Empty<string>();
    }

    public async Task<bool> LoginAsync(string userNameOrEmail, string password, CancellationToken ct = default)
    {
        var result = await SendCoreAsync<TokenResponse>(
            HttpMethod.Post, "api/auth/login", new { userNameOrEmail, password },
            authenticated: false, allowRefresh: false, ct);

        if (result is null || !result.Success || result.Data is null)
        {
            throw new ApiException(result?.Message ?? "Login failed. Check credentials.");
        }

        AccessToken = result.Data.AccessToken;
        RefreshToken = result.Data.RefreshToken;
        await LoadCurrentUserAsync(ct);
        CacheTokens();
        return true;
    }

    public async Task LoadCurrentUserAsync(CancellationToken ct = default)
    {
        try
        {
            var me = await GetAsync<CurrentUser>("api/auth/me", ct);
            if (me is not null)
            {
                UserId = me.Id;
                FullName = me.FullName;
                Email = me.Email;
                Roles = me.Roles ?? new List<string>();
                CacheTokens();
            }
        }
        catch (Exception)
        {
            // Non-fatal: session still usable with token only.
        }
    }

    public async Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        var response = await SendCoreAsync<T>(HttpMethod.Get, path, null, authenticated: true, allowRefresh: true, ct).ConfigureAwait(false);
        return ExtractData(response);
    }

    public Task<T?> PostAsync<T>(string path, object? payload = null, CancellationToken ct = default)
        => SendCoreAsync<T>(HttpMethod.Post, path, payload, authenticated: true, allowRefresh: true, ct)
            .ContinueWith(t => ExtractData(t.Result), ct);

    public Task<T?> PutAsync<T>(string path, object? payload = null, CancellationToken ct = default)
        => SendCoreAsync<T>(HttpMethod.Put, path, payload, authenticated: true, allowRefresh: true, ct)
            .ContinueWith(t => ExtractData(t.Result), ct);

    public async Task PostVoidAsync(string path, object? payload = null, CancellationToken ct = default)
    {
        var result = await SendCoreAsync<object?>(HttpMethod.Post, path, payload, authenticated: true, allowRefresh: true, ct);
        if (result is null) throw new ApiException("No server response.");
        if (!result.Success) throw new ApiException(result.Message ?? "Request failed.");
    }

    public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ResolveUrl(url));
        if (IsAuthenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        }

        var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException($"Download failed ({response.StatusCode}) for {url}.", (int)response.StatusCode);
        }
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<T?> UploadAsync<T>(string path, byte[] bytes, string fileName, string contentType, CancellationToken ct = default)
        where T : class
    {
        using var content = new MultipartFormDataContent();
        using var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveUrl(path)) { Content = content };
        if (IsAuthenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        }

        var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException($"Upload failed ({response.StatusCode}) for {ResolveUrl(path)}: {json}", (int)response.StatusCode);
        }

        var result = JsonSerializer.Deserialize<ApiResponse<T>>(json, JsonOptions);
        return ExtractData(result);
    }

    private string ResolveUrl(string url)
        => url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{_http.BaseAddress!.ToString().TrimEnd('/')}/{url.TrimStart('/')}";

    private static T2? ExtractData<T2>(ApiResponse<T2>? response)
    {
        if (response is null) return default;
        if (!response.Success) throw new ApiException(response.Message ?? "Request failed.");
        return response.Data;
    }

    private async Task<ApiResponse<T>?> SendCoreAsync<T>(
        HttpMethod method, string path, object? payload, bool authenticated, bool allowRefresh, CancellationToken ct)
    {
        if (authenticated)
        {
            await EnsureAuthenticatedAsync(ct);
            if (string.IsNullOrWhiteSpace(AccessToken))
            {
                throw new ApiException("Not authenticated.");
            }
        }

        var response = await SendRawAsync(method, path, payload, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && allowRefresh
            && !string.IsNullOrWhiteSpace(RefreshToken))
        {
            if (await TryRefreshAsync(ct))
            {
                response.Dispose();
                response = await SendRawAsync(method, path, payload, ct);
            }
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException(
                $"Request failed ({response.StatusCode}) for {method} {ResolveUrl(path)}: {json}",
                (int)response.StatusCode);
        }

        try
        {
            return JsonSerializer.Deserialize<ApiResponse<T>>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ApiException($"Invalid server response: {ex.Message}");
        }
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ResolveUrl(path));
        if (IsAuthenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        }

        if (payload is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        }
        return await _http.SendAsync(request, ct);
    }

    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(RefreshToken)) return false;

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ResolveUrl("api/auth/refresh"))
            {
                Content = new StringContent(JsonSerializer.Serialize(new { refreshToken = RefreshToken }, JsonOptions), Encoding.UTF8, "application/json")
            };
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception)
        {
            return false;
        }

        if (!response.IsSuccessStatusCode)
        {
            ClearSession();
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<ApiResponse<TokenResponse>>(json, JsonOptions);
        if (result?.Data is null)
        {
            ClearSession();
            return false;
        }

        AccessToken = result.Data.AccessToken;
        RefreshToken = result.Data.RefreshToken;
        CacheTokens();
        await PersistPrincipalAsync(ct);
        return true;
    }
}