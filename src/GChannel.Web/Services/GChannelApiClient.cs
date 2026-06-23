using System.Net.Http.Headers;
using System.Net.Http.Json;
using GChannel.Shared.Contracts;
using Microsoft.AspNetCore.Components.Authorization;

namespace GChannel.Web.Services;

/// <summary>
/// Typed client the UI uses to talk to the API service. It transparently attaches the
/// signed-in user's Google access token so Razor components never touch tokens or REST paths.
/// </summary>
public sealed class GChannelApiClient(
    HttpClient http,
    AuthenticationStateProvider authState,
    GoogleTokenProvider tokenProvider)
{
    public async Task<CheckCloudIdentityResult?> CheckCloudIdentityAsync(
        CheckCloudIdentityRequest request,
        CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, ApiRoutes.CheckCloudIdentity)
        {
            Content = JsonContent.Create(request)
        };

        await AttachGoogleTokenAsync(message);

        using var response = await http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CheckCloudIdentityResult>(cancellationToken);
    }

    private async Task AttachGoogleTokenAsync(HttpRequestMessage message)
    {
        var state = await authState.GetAuthenticationStateAsync();
        var token = await tokenProvider.GetAccessTokenAsync(state.User);
        if (!string.IsNullOrEmpty(token))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var email = state.User.Identity?.Name;
        if (!string.IsNullOrEmpty(email))
        {
            message.Headers.Add("X-User-Email", email);
        }
    }
}
