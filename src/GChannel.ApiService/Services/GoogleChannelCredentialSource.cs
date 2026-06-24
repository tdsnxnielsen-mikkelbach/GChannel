using GChannel.ApiService.Configuration;
using Google.Apis.Auth.OAuth2;
using Microsoft.Net.Http.Headers;

namespace GChannel.ApiService.Services;

/// <summary>
/// Supplies the <see cref="GoogleCredential"/> the <see cref="GoogleChannelClient"/> uses to call the
/// Channel API. Abstracted so the same client can run either against the signed-in user's forwarded
/// token (normal requests) or a service-account credential (the background dashboard refresher).
/// </summary>
public interface IGoogleChannelCredentialSource
{
    GoogleCredential GetCredential();
}

/// <summary>
/// Default source: uses the Google OAuth access token the Blazor front end forwards as a Bearer
/// header on the inbound request.
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

/// <summary>
/// Service-account source used off the request path (e.g. the background refresher). The Channel API
/// has no service-account identity of its own, so the key impersonates a reseller admin user via
/// domain-wide delegation. The credential is built once and reused; tokens are fetched lazily on first
/// use and refreshed by the Google client library.
/// </summary>
public sealed class ServiceAccountCredentialSource : IGoogleChannelCredentialSource
{
    private readonly GoogleCredential _credential;

    public ServiceAccountCredentialSource(GoogleChannelOptions options)
    {
        if (!options.HasServiceAccountCredential)
        {
            throw new InvalidOperationException(
                "No service-account credential is configured (GoogleChannel:ServiceAccountKeyJson or ServiceAccountKeyPath).");
        }

        if (string.IsNullOrWhiteSpace(options.ImpersonateUser))
        {
            throw new InvalidOperationException(
                "GoogleChannel:ImpersonateUser is required for the service-account credential (domain-wide delegation).");
        }

        var baseCredential = string.IsNullOrWhiteSpace(options.ServiceAccountKeyJson)
            ? CredentialFactory.FromFile(options.ServiceAccountKeyPath, "service_account")
            : CredentialFactory.FromJson(options.ServiceAccountKeyJson, "service_account");

        _credential = baseCredential
            .CreateScoped(GoogleChannelOptions.ChannelScope)
            .CreateWithUser(options.ImpersonateUser);
    }

    public GoogleCredential GetCredential() => _credential;
}
