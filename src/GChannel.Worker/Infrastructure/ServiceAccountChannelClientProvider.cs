using GChannel.Core.Configuration;
using GChannel.Core.GoogleApis;
using Microsoft.Extensions.Options;

namespace GChannel.Worker.Infrastructure;

/// <summary>
/// Builds (once, lazily) the service-account-backed <see cref="GoogleChannelClient"/> the worker uses off the
/// request path. The Channel API has no service-account identity of its own, so the key impersonates a
/// reseller admin via domain-wide delegation. Returns null when the credential / impersonation user is not
/// configured, in which case every handler that needs the Channel API is a no-op.
/// </summary>
public sealed class ServiceAccountChannelClientProvider(
    IOptions<GoogleChannelOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<ServiceAccountChannelClientProvider> logger)
{
    private readonly Lazy<IGoogleChannelClient?> _client = new(() =>
    {
        var opts = options.Value;
        if (!opts.HasServiceAccountCredential || string.IsNullOrWhiteSpace(opts.ImpersonateUser))
        {
            logger.LogInformation("No service-account credential + impersonation user configured; Channel API background work is disabled.");
            return null;
        }
        return new GoogleChannelClient(new ServiceAccountCredentialSource(opts), options, loggerFactory.CreateLogger<GoogleChannelClient>());
    });

    public IGoogleChannelClient? Client => _client.Value;

    public bool IsConfigured => options.Value.HasServiceAccountCredential && !string.IsNullOrWhiteSpace(options.Value.ImpersonateUser);
}
