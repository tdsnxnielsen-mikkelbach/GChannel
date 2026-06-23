using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Cloudchannel.v1;
using Google.Apis.Cloudchannel.v1.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace GChannel.ApiService.Services;

/// <summary>
/// Abstraction over the Google Cloud Channel API. The UI talks to these methods only and
/// never sees the underlying REST shapes.
/// </summary>
public interface IGoogleChannelClient
{
    Task<CheckCloudIdentityResult> CheckCloudIdentityAsync(CheckCloudIdentityRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Builds a per-request <see cref="CloudchannelService"/> using the caller's Google OAuth
/// access token (forwarded as a Bearer token by the Blazor front end).
/// </summary>
public sealed class GoogleChannelClient(
    IHttpContextAccessor httpContextAccessor,
    IOptions<GoogleChannelOptions> options,
    ILogger<GoogleChannelClient> logger) : IGoogleChannelClient
{
    private readonly GoogleChannelOptions _options = options.Value;

    public async Task<CheckCloudIdentityResult> CheckCloudIdentityAsync(
        CheckCloudIdentityRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Domain);
        EnsureAccountConfigured();

        using var service = CreateService();

        var body = new GoogleCloudChannelV1CheckCloudIdentityAccountsExistRequest
        {
            Domain = request.Domain,
            PrimaryAdminEmail = request.PrimaryAdminEmail
        };

        logger.LogInformation("Checking Cloud Identity accounts for domain {Domain}", request.Domain);

        var response = await service.Accounts
            .CheckCloudIdentityAccountsExist(body, _options.AccountName)
            .ExecuteAsync(cancellationToken);

        var accounts = (response.CloudIdentityAccounts ?? [])
            .Select(a => new CloudIdentityAccount
            {
                Existing = a.Existing ?? false,
                Owned = a.Owned ?? false,
                CustomerName = a.CustomerName,
                CustomerCloudIdentityId = a.CustomerCloudIdentityId,
                CustomerType = a.CustomerType,
                ChannelPartnerCloudIdentityId = a.ChannelPartnerCloudIdentityId
            })
            .ToList();

        return new CheckCloudIdentityResult
        {
            Domain = request.Domain,
            Exists = accounts.Any(a => a.Existing),
            Accounts = accounts
        };
    }

    private CloudchannelService CreateService()
    {
        var credential = GoogleCredential.FromAccessToken(GetAccessToken());
        return new CloudchannelService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = _options.ApplicationName
        });
    }

    private string GetAccessToken()
    {
        var header = httpContextAccessor.HttpContext?.Request.Headers[HeaderNames.Authorization].ToString();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new MissingGoogleTokenException();
        }

        return header["Bearer ".Length..].Trim();
    }

    private void EnsureAccountConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.AccountName))
        {
            throw new InvalidOperationException(
                "GoogleChannel:AccountId is not configured. Set the reseller account resource name (accounts/...).");
        }
    }
}

/// <summary>Thrown when the inbound request carries no Google access token.</summary>
public sealed class MissingGoogleTokenException()
    : InvalidOperationException("No Google access token was supplied on the request.");
