using GChannel.Core.Configuration;
using Google.Apis.Auth.OAuth2;

namespace GChannel.Core.GoogleApis;

/// <summary>
/// Supplies the <see cref="GoogleCredential"/> the <see cref="GoogleChannelClient"/> uses to call the
/// Channel API. Abstracted so the same client can run either against the signed-in user's forwarded
/// token (normal requests — see <c>RequestTokenCredentialSource</c> in the API host) or a
/// service-account credential (the background worker).
/// </summary>
public interface IGoogleChannelCredentialSource
{
    GoogleCredential GetCredential();
}

/// <summary>
/// Service-account source used off the request path (the background worker). The Channel API
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
