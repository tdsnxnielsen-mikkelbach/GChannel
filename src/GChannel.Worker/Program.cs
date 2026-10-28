using GChannel.Core.DependencyInjection;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence;
using GChannel.Worker.Handlers;
using GChannel.Worker.Infrastructure;
using GChannel.Worker.Services;
using JasperFx.Events.Daemon;
using Wolverine;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();

// Azure Database for PostgreSQL via Aspire ("gchanneldb"; Microsoft Entra ID token auth in Azure) hosting the
// Marten document store + event store and Wolverine's durable inbox/outbox + saga storage. The worker also runs
// Marten's async projection daemon (HotCold: exactly one node owns it — leader election via Postgres).
builder.AddGChannelMarten()
    .AddAsyncDaemon(DaemonMode.HotCold);

// Redis (Aspire connection "cache"): the API's Google-response cache, which the worker warms (live dashboard
// refresh) and invalidates (read-model dashboard keys). Entra ID auth for Azure Managed Redis.
builder.AddRedisClientBuilder("cache")
    .WithAzureAuthentication()
    .WithDistributedCache();

// Shared options + Google/Ion integration services (also registered by the API).
builder.Services.AddGChannelCore(builder.Configuration);

// The worker calls the Channel API with the service-account credential (domain-wide delegation).
builder.Services.AddSingleton<ServiceAccountChannelClientProvider>();

// Recurring jobs (estate sync, live dashboard refresh, billing-export sync, posture scan) are self-rescheduling
// Wolverine messages on the Service Bus "jobs" queue; the bootstrap starts/repairs their chains.
builder.Services.AddSingleton<RecurringJobCatalog>();
builder.Services.AddHostedService<RecurringJobsBootstrap>();

// Google Pub/Sub → Service Bus bridge for Channel change notifications.
builder.Services.AddHostedService<ChannelNotificationsService>();

// Wolverine: handlers for the estate-events subscription and the estate-sync / jobs / operations queues.
builder.UseWolverine(opts => opts.ConfigureGChannelMessaging("gchannel-worker", MessagingRole.Worker, typeof(ProjectCustomerHandler).Assembly));

var host = builder.Build();
host.Run();
