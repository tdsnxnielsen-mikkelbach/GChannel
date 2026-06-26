using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Endpoints;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GoogleApiExceptionHandler>();
builder.Services.AddHttpContextAccessor();

// Azure SQL (serverless) via Aspire — connection name must match AppHost ("gchanneldb").
builder.AddSqlServerDbContext<GChannelDbContext>("gchanneldb");

// Redis client (IConnectionMultiplexer) + distributed cache via Aspire — connection name must match
// AppHost ("cache"). WithAzureAuthentication enables Microsoft Entra ID (managed identity) auth for
// Azure Managed Redis, which is provisioned with access keys disabled; it is a no-op for the local
// RunAsContainer cache (which uses a password). The IConnectionMultiplexer is used by the background
// dashboard refresher to take a cluster-wide lock so only one replica recomputes per interval.
builder.AddRedisClientBuilder("cache")
    .WithAzureAuthentication()
    .WithDistributedCache();

builder.Services
    .AddOptions<GoogleChannelOptions>()
    .Bind(builder.Configuration.GetSection(GoogleChannelOptions.SectionName));

builder.Services.AddScoped<IGoogleChannelCredentialSource, RequestTokenCredentialSource>();
builder.Services.AddScoped<IGoogleChannelClient, GoogleChannelClient>();

// Keeps the dashboard summary cache warm out-of-band using a service account (no-op unless
// GoogleChannel service-account + impersonation user + BackgroundRefreshSeconds are configured).
builder.Services.AddHostedService<DashboardRefreshService>();

// Streams Channel change notifications from Pub/Sub into a capped Redis feed (no-op unless
// GoogleChannel PubSubProjectId + PubSubSubscriptionId + a service-account key are configured).
builder.Services.AddHostedService<ChannelNotificationsService>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await EnsureDatabaseAsync(app);

if (app.Environment.IsDevelopment())
{
    await SeedNotificationsForDevAsync(app);
}

app.MapDefaultEndpoints();
app.MapAccountsEndpoints();
app.MapCatalogEndpoints();
app.MapCustomersEndpoints();
app.MapEntitlementsEndpoints();
app.MapTransfersEndpoints();
app.MapChannelPartnerLinksEndpoints();
app.MapRepricingEndpoints();
app.MapOperationsEndpoints();
app.MapNotificationsEndpoints();
app.MapDashboardEndpoints();

app.Run();

static async Task EnsureDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
    try
    {
        await db.Database.EnsureCreatedAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database initialization was skipped (the server may be paused or unreachable).");
    }
}

// Development-only convenience: seed the notification feed with a couple of sample Channel events when
// it is empty, so the Notifications page shows data without a live Pub/Sub subscription. Never runs in
// production. The sample resource names resolve to real customers in dev so names display correctly.
static async Task SeedNotificationsForDevAsync(WebApplication app)
{
    try
    {
        var redis = app.Services.GetRequiredService<IConnectionMultiplexer>();
        var db = redis.GetDatabase();
        if (await db.KeyExistsAsync(ChannelNotificationsService.FeedKey))
        {
            return; // Don't clobber a feed that already has (real or previously seeded) events.
        }

        var options = app.Services.GetRequiredService<IOptions<GoogleChannelOptions>>().Value;
        var account = string.IsNullOrWhiteSpace(options.AccountName) ? "accounts/C03r1rwb0" : options.AccountName;

        var samples = new[]
        {
            ("SV4YDIKAbIBzO8", "SvelaOSyAWM8Sz"),
            ("SV4YDIKAbIBzO8", "Slx8Y3wbAoWzOn")
        };

        foreach (var (customerId, entitlementId) in samples)
        {
            var notification = new ChannelNotification
            {
                Kind = "Entitlement",
                EventType = "LICENSE_ASSIGNMENT_CHANGED",
                ResourceName = $"{account}/customers/{customerId}/entitlements/{entitlementId}",
                CustomerId = customerId,
                EntitlementId = entitlementId,
                MessageId = null,
                ReceivedAt = DateTimeOffset.UtcNow
            };

            await db.ListLeftPushAsync(ChannelNotificationsService.FeedKey, JsonSerializer.Serialize(notification));
        }

        app.Logger.LogInformation("Seeded {Count} sample notifications into the development feed.", samples.Length);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Development notification seeding was skipped.");
    }
}
