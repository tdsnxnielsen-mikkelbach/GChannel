using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Services;
using GChannel.Worker.Services;
using Microsoft.EntityFrameworkCore;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Azure SQL (serverless) via Aspire — connection name must match AppHost ("gchanneldb").
// The serverless DB auto-pauses when idle; a single connection must survive the ~30-60s resume, so raise
// the connect timeout well past the 15s default, and retry transient failures (incl. the -2 timeout).
builder.AddSqlServerDbContext<GChannelDbContext>("gchanneldb",
    configureSettings: settings =>
    {
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString)
            && !settings.ConnectionString.Contains("Connect Timeout", StringComparison.OrdinalIgnoreCase)
            && !settings.ConnectionString.Contains("Connection Timeout", StringComparison.OrdinalIgnoreCase))
        {
            settings.ConnectionString += ";Connect Timeout=90";
        }
    },
    configureDbContextOptions: options => options.UseSqlServer(sql =>
        sql.EnableRetryOnFailure(maxRetryCount: 8, maxRetryDelay: TimeSpan.FromSeconds(20), errorNumbersToAdd: new[] { -2 })));

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

// §10 read-model projection helper — shared by the bulk sync and the Pub/Sub event-driven projection
// so every path denormalises identical fields. Stateless (takes the DbContext/client per call).
builder.Services.AddSingleton<ReadModelProjector>();

// The estate/dashboard background workers, extracted from the API container so they scale on their
// own axis (fixed single replica) instead of with HTTP traffic. Each is a no-op unless the relevant
// GoogleChannel options + service-account credential are configured, and each takes a Redis lock so
// extra replicas stay idle. Schema creation stays with the API on startup.
builder.Services.AddHostedService<DashboardRefreshService>();
builder.Services.AddHostedService<ChannelNotificationsService>();
builder.Services.AddHostedService<ReadModelSyncService>();

var host = builder.Build();
host.Run();
