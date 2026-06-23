using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace GChannel.Web.Services;

/// <summary>
/// Supplies a valid Google access token for the signed-in user, silently refreshing it via the
/// captured refresh token (<c>AccessType=offline</c>) once the token minted at sign-in expires.
/// Refreshed tokens are cached in memory per user so a refresh happens at most once per token
/// lifetime. The long-lived refresh token never leaves the Web app — only short-lived access
/// tokens are forwarded to the API service.
/// </summary>
public sealed class GoogleTokenProvider(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    IConfiguration configuration,
    ILogger<GoogleTokenProvider> logger)
{
    /// <summary>Claim holding the access token captured at sign-in.</summary>
    public const string AccessTokenClaim = "google_access_token";

    /// <summary>Claim holding the long-lived refresh token captured at sign-in.</summary>
    public const string RefreshTokenClaim = "google_refresh_token";

    /// <summary>Claim holding the round-trip (UTC) expiry of the sign-in access token.</summary>
    public const string ExpiresAtClaim = "google_access_token_expires_at";

    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    // Treat a token as expired slightly early so it is never sent within this window of expiry.
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Returns a currently-valid Google access token for <paramref name="user"/>, refreshing it
    /// silently if required. Returns <c>null</c> when no token is available.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.Identity?.Name;
        var initialToken = user.FindFirst(AccessTokenClaim)?.Value;

        // No stable per-user key — fall back to whatever token was captured at sign-in.
        if (string.IsNullOrEmpty(subject))
        {
            return initialToken;
        }

        var cacheKey = $"google_at:{subject}";
        if (cache.TryGetValue(cacheKey, out string? cached) && !string.IsNullOrEmpty(cached))
        {
            return cached;
        }

        // The token captured at sign-in is still valid — use it until just before it expires.
        if (!string.IsNullOrEmpty(initialToken)
            && DateTimeOffset.TryParse(user.FindFirst(ExpiresAtClaim)?.Value, out var expiresAt)
            && expiresAt - ExpirySkew > DateTimeOffset.UtcNow)
        {
            cache.Set(cacheKey, initialToken, expiresAt - ExpirySkew);
            return initialToken;
        }

        // Otherwise silently refresh using the captured refresh token.
        var refreshToken = user.FindFirst(RefreshTokenClaim)?.Value;
        if (string.IsNullOrEmpty(refreshToken))
        {
            // Best effort: hand back the (likely expired) token; the API will reject it if so.
            return initialToken;
        }

        var refreshed = await RefreshAsync(refreshToken, cancellationToken);
        if (refreshed is null)
        {
            return initialToken;
        }

        var lifetime = TimeSpan.FromSeconds(Math.Max(60, refreshed.ExpiresInSeconds)) - ExpirySkew;
        cache.Set(cacheKey, refreshed.AccessToken, lifetime);
        return refreshed.AccessToken;
    }

    private async Task<RefreshedToken?> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var clientId = configuration["Authentication:Google:ClientId"];
        var clientSecret = configuration["Authentication:Google:ClientSecret"];
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            logger.LogWarning("Cannot refresh Google access token: client id/secret not configured.");
            return null;
        }

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        });

        var http = httpClientFactory.CreateClient();
        using var response = await http.PostAsync(TokenEndpoint, content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Google token refresh failed with status {Status}.", response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<GoogleTokenResponse>(cancellationToken);
        if (payload?.AccessToken is null)
        {
            return null;
        }

        return new RefreshedToken(payload.AccessToken, payload.ExpiresIn);
    }

    private sealed record RefreshedToken(string AccessToken, int ExpiresInSeconds);

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }

        [JsonPropertyName("token_type")]
        public string? TokenType { get; set; }
    }
}
