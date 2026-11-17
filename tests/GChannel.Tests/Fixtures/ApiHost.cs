using Alba;
using GChannel.Core.GoogleApis;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Wolverine;

namespace GChannel.Tests.Fixtures;

/// <summary>
/// Boots the real API host (Marten schema on the Testcontainers PostgreSQL, Wolverine.HTTP endpoints, handlers)
/// with the external pieces replaced: Service Bus transport stubbed, Redis swapped for an in-memory
/// IDistributedCache, and the Google Channel client substituted so no Google call is ever made.
/// </summary>
public static class ApiHost
{
    /// <summary>Settings that make the BigQuery billing export count as configured (nothing is ever queried).</summary>
    public static readonly IReadOnlyDictionary<string, string?> BillingExportConfigured = new Dictionary<string, string?>
    {
        ["GoogleBilling:BigQueryEnabled"] = "true",
        ["GoogleBilling:BigQueryProjectId"] = "test-project",
        ["GoogleBilling:BillingExportDataset"] = "billing_export",
        ["GoogleBilling:BillingExportTable"] = "gcp_billing_export_test",
    };

    /// <param name="settings">Extra configuration, applied after the defaults below.</param>
    public static Task<IAlbaHost> StartAsync(string connectionString, IGoogleChannelClient channel, IReadOnlyDictionary<string, string?>? settings = null)
        => AlbaHost.For<GChannel.ApiService.ApiAssemblyMarker>(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:gchanneldb", connectionString);
            builder.UseSetting("ConnectionStrings:messaging", WorkerHost.DummyServiceBus);
            builder.UseSetting("ConnectionStrings:cache", "localhost:6379,abortConnect=false");
            builder.UseSetting("GoogleChannel:AccountId", "accounts/C0test");
            builder.UseSetting("GoogleChannel:UseReadModel", "true");
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureServices(services =>
            {
                services.DisableAllExternalWolverineTransports();
                services.RemoveAll<IDistributedCache>();
                services.AddDistributedMemoryCache();
                services.RemoveAll<IGoogleChannelClient>();
                services.AddSingleton(channel);
            });
        });
}
