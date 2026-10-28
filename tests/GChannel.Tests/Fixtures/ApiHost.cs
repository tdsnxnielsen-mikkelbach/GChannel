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
    public static Task<IAlbaHost> StartAsync(string connectionString, IGoogleChannelClient channel)
        => AlbaHost.For<GChannel.ApiService.ApiAssemblyMarker>(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:gchanneldb", connectionString);
            builder.UseSetting("ConnectionStrings:messaging", "Endpoint=sb://localhost;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==;UseDevelopmentEmulator=true;");
            builder.UseSetting("ConnectionStrings:cache", "localhost:6379,abortConnect=false");
            builder.UseSetting("GoogleChannel:AccountId", "accounts/C0test");
            builder.UseSetting("GoogleChannel:UseReadModel", "true");

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
