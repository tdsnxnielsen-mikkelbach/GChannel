using GChannel.Core.Configuration;
using GChannel.Core.GoogleApis;
using GChannel.Core.Ion;
using Google.Apis.Auth.OAuth2;
using Microsoft.Net.Http.Headers;

namespace GChannel.ApiService.Services;

/// <summary>
/// Default credential source for the API host: uses the Google OAuth access token the Blazor front end
/// forwards as a <c>Bearer</c> header on the inbound request.
/// </summary>
public sealed class RequestTokenCredentialSource(IHttpContextAccessor httpContextAccessor)
    : IGoogleChannelCredentialSource
{
    public GoogleCredential GetCredential()
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new MissingGoogleTokenException();
        }

        return GoogleCredential.FromAccessToken(header["Bearer ".Length..].Trim());
    }
}
