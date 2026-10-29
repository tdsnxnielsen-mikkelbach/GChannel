using GChannel.Core.DependencyInjection;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence;
using GChannel.Worker.Handlers;
using GChannel.Worker.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace GChannel.Tests.Fixtures;

/// <summary>
/// A worker-shaped host: Marten (schema applied on the Testcontainers PostgreSQL), the shared Core services and
/// Wolverine with the worker's handlers/saga discovered and every external transport stubbed. No Google
/// service-account credential is configured, so Channel-API-dependent jobs report themselves disabled.
/// </summary>
public static class WorkerHost
{
    // A syntactically valid Service Bus connection string so Wolverine's transport configures; it is never
    // connected (every external transport is stubbed), so no namespace is involved in tests.
    public const string DummyServiceBus = "Endpoint=sb://localhost;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==;";

    public static async Task<IHost> StartAsync(string connectionString, Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = "Testing";
        builder.Configuration["ConnectionStrings:gchanneldb"] = connectionString;
        builder.Configuration["ConnectionStrings:messaging"] = DummyServiceBus;
        builder.Configuration["GoogleChannel:AccountId"] = "accounts/C0test";

        builder.AddGChannelMarten(messageStorageSchema: "wolverine_tests")
            .InitializeWith<StrayReadModelCleanup>();
        builder.Services.AddGChannelCore(builder.Configuration);
        builder.Services.AddDistributedMemoryCache();
        builder.Services.AddSingleton<ServiceAccountChannelClientProvider>();
        builder.Services.AddSingleton<RecurringJobCatalog>();

        builder.UseWolverine(opts => opts.ConfigureGChannelMessaging("gchannel-tests", MessagingRole.Worker, typeof(ProjectCustomerHandler).Assembly));
        builder.Services.DisableAllExternalWolverineTransports();

        configure?.Invoke(builder);

        var host = builder.Build();
        await host.StartAsync();
        return host;
    }
}
