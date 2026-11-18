using GChannel.Core.Configuration;
using GChannel.Core.Estate;
using GChannel.Core.GoogleApis;
using GChannel.Core.Ion;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GChannel.Core.DependencyInjection;

public static class GChannelCoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the options and Google / Ion integration services that both hosts (API and worker)
    /// share. Host-specific registrations (the Channel API credential source, the scoped
    /// <see cref="IGoogleChannelClient"/>, persistence and messaging) stay in each <c>Program.cs</c>.
    /// </summary>
    public static IServiceCollection AddGChannelCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<GoogleChannelOptions>()
            .Bind(configuration.GetSection(GoogleChannelOptions.SectionName));
        services.AddOptions<GoogleBillingOptions>()
            .Bind(configuration.GetSection(GoogleBillingOptions.SectionName));
        services.AddOptions<IonGatewayOptions>()
            .Bind(configuration.GetSection(IonGatewayOptions.SectionName));

        // Ion Gateway (TD SYNNEX StreamOne) enrichment — singleton over IHttpClientFactory so it is safe to
        // inject into singleton projection/sync services.
        services.AddHttpClient(IonGatewayClient.HttpClientName);
        services.AddSingleton<IIonGatewayClient, IonGatewayClient>();

        // Cloud Billing budgets (billingbudgets.googleapis.com) — reuses the reseller service-account key;
        // the gRPC clients it builds are thread-safe and cached, so a singleton is appropriate.
        services.AddSingleton<IBillingBudgetsService, BillingBudgetsService>();

        // Admin SDK (Directory + Reports) via domain-wide delegation — per-user 2SV / Advanced Protection
        // for billing IAM members. Stateless clients, cached credential: singleton.
        services.AddSingleton<IWorkspaceUserSecurityService, WorkspaceUserSecurityService>();

        // Cloud Commerce Marketplace orders (REST over IHttpClientFactory).
        services.AddHttpClient(MarketplaceOrdersService.HttpClientName);
        services.AddSingleton<IMarketplaceOrdersService, MarketplaceOrdersService>();

        // Billing IAM member identity / security posture resolution (API on-demand + worker nightly scan).
        services.AddSingleton<MemberIdentityResolver>();

        // The estate observer: diffs live Channel API state into Marten events. Stateless (takes the session
        // per call) over singleton dependencies.
        services.AddSingleton<EstateObserver>();

        return services;
    }
}
