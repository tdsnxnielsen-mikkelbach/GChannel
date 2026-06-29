using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Azure SQL (serverless) via Aspire — connection name must match AppHost ("gchanneldb").
builder.AddSqlServerDbContext<GChannelDbContext>("gchanneldb");

// Redis client (IConnectionMultiplexer) + distributed cache via Aspire — connection name must match
// AppHost ("cache"). WithAzureAuthentication enables Microsoft Entra ID (managed identity) auth for
// Azure Managed Redis; it is a no-op for the local container cache (password). The multiplexer backs
// the cluster-wide single-flight locks each worker takes so only one replica syncs per interval.
builder.AddRedisClientBuilder("cache")
    .WithAzureAuthentication()
    .WithDistributedCache();

builder.Services
    .AddOptions<GoogleChannelOptions>()
    .Bind(builder.Configuration.GetSection(GoogleChannelOptions.SectionName));

// The estate/dashboard background workers, extracted from the API container so they scale on their
// own axis (fixed single replica) instead of with HTTP traffic. Each is a no-op unless the relevant
// GoogleChannel options + service-account credential are configured, and each takes a Redis lock so
// extra replicas stay idle. Schema creation stays with the API on startup.
builder.Services.AddHostedService<DashboardRefreshService>();
builder.Services.AddHostedService<ChannelNotificationsService>();
builder.Services.AddHostedService<ReadModelSyncService>();

var host = builder.Build();
host.Run();
