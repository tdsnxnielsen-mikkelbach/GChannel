using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Endpoints;
using GChannel.ApiService.Services;
using GChannel.Shared.Contracts;
using Microsoft.EntityFrameworkCore;
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

// The background workers (dashboard refresh, Pub/Sub subscriber, read-model sync) have been extracted
// into the GChannel.Worker container so they scale independently of HTTP traffic (fixed single replica)
// and the API can scale to zero. The API still owns the read schema (created on startup) and serves the
// estate/dashboard endpoints from the cache + SQL those workers populate.

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
app.MapEstateEndpoints();

app.Run();

static async Task EnsureDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<GChannelDbContext>();
    try
    {
        await db.Database.EnsureCreatedAsync();
        // EnsureCreated only builds the whole schema on a fresh database, so additively create the
        // §10 read-model tables if they're missing on a database that already existed (which holds
        // only the IdentityCheckLogs audit table). Idempotent: each statement guards on OBJECT_ID.
        await EnsureReadModelTablesAsync(db);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database initialization was skipped (the server may be paused or unreachable).");
    }
}

// Additive, idempotent creation of the §10 read-model tables for databases created before they
// existed. Avoids EF migrations (the app uses EnsureCreated): each CREATE is guarded by OBJECT_ID.
static async Task EnsureReadModelTablesAsync(GChannelDbContext db)
{
    const string sql = """
        IF OBJECT_ID('ResellerLinks', 'U') IS NULL
        CREATE TABLE ResellerLinks (
            LinkId nvarchar(128) NOT NULL PRIMARY KEY,
            ResellerCloudId nvarchar(128) NULL,
            PrimaryDomain nvarchar(255) NULL,
            LinkState nvarchar(32) NOT NULL,
            CustomerCount int NOT NULL,
            CreateTime datetimeoffset NULL,
            LastSyncedUtc datetimeoffset NOT NULL,
            SyncError nvarchar(512) NULL
        );
        IF OBJECT_ID('CustomerRecords', 'U') IS NULL
        CREATE TABLE CustomerRecords (
            CustomerId nvarchar(128) NOT NULL PRIMARY KEY,
            OrgName nvarchar(512) NULL,
            Domain nvarchar(255) NULL,
            CloudIdentityId nvarchar(128) NULL,
            OwningLinkId nvarchar(128) NULL,
            CreateTime datetimeoffset NULL,
            LastSyncedUtc datetimeoffset NOT NULL,
            SeatCount bigint NOT NULL CONSTRAINT DF_CustomerRecords_SeatCount DEFAULT 0,
            IsDeleted bit NOT NULL
        );
        IF OBJECT_ID('SyncCursors', 'U') IS NULL
        CREATE TABLE SyncCursors (
            Scope nvarchar(64) NOT NULL PRIMARY KEY,
            LastFullPassUtc datetimeoffset NULL,
            LastCycleUtc datetimeoffset NULL,
            Notes nvarchar(512) NULL
        );
        IF OBJECT_ID('EntitlementRecords', 'U') IS NULL
        CREATE TABLE EntitlementRecords (
            EntitlementId nvarchar(128) NOT NULL PRIMARY KEY,
            CustomerId nvarchar(128) NOT NULL,
            OwningLinkId nvarchar(128) NULL,
            ProductId nvarchar(128) NULL,
            ProductName nvarchar(255) NULL,
            SkuId nvarchar(128) NULL,
            OfferId nvarchar(128) NULL,
            State nvarchar(32) NOT NULL,
            Seats bigint NOT NULL,
            BillableSeats bigint NOT NULL CONSTRAINT DF_EntitlementRecords_BillableSeats DEFAULT 0,
            IsTrial bit NOT NULL,
            UnitPrice decimal(18,6) NOT NULL CONSTRAINT DF_EntitlementRecords_UnitPrice DEFAULT 0,
            Currency nvarchar(8) NULL,
            RepricingPercent decimal(9,4) NOT NULL CONSTRAINT DF_EntitlementRecords_RepricingPercent DEFAULT 0,
            LastSyncedUtc datetimeoffset NOT NULL,
            IsDeleted bit NOT NULL
        );
        IF COL_LENGTH('CustomerRecords','SeatCount') IS NULL
        ALTER TABLE CustomerRecords ADD SeatCount bigint NOT NULL CONSTRAINT DF_CustomerRecords_SeatCount DEFAULT 0;
        IF COL_LENGTH('EntitlementRecords','UnitPrice') IS NULL
        ALTER TABLE EntitlementRecords ADD UnitPrice decimal(18,6) NOT NULL CONSTRAINT DF_EntitlementRecords_UnitPrice DEFAULT 0;
        IF COL_LENGTH('EntitlementRecords','Currency') IS NULL
        ALTER TABLE EntitlementRecords ADD Currency nvarchar(8) NULL;
        IF COL_LENGTH('EntitlementRecords','RepricingPercent') IS NULL
        ALTER TABLE EntitlementRecords ADD RepricingPercent decimal(9,4) NOT NULL CONSTRAINT DF_EntitlementRecords_RepricingPercent DEFAULT 0;
        IF COL_LENGTH('EntitlementRecords','ProductName') IS NULL
        ALTER TABLE EntitlementRecords ADD ProductName nvarchar(255) NULL;
        IF COL_LENGTH('EntitlementRecords','SkuName') IS NULL
        ALTER TABLE EntitlementRecords ADD SkuName nvarchar(255) NULL;
        IF COL_LENGTH('EntitlementRecords','OfferName') IS NULL
        ALTER TABLE EntitlementRecords ADD OfferName nvarchar(255) NULL;
        IF COL_LENGTH('EntitlementRecords','CreateTime') IS NULL
        ALTER TABLE EntitlementRecords ADD CreateTime datetimeoffset NULL;
        IF COL_LENGTH('EntitlementRecords','CommitmentEndTime') IS NULL
        ALTER TABLE EntitlementRecords ADD CommitmentEndTime datetimeoffset NULL;
        IF COL_LENGTH('EntitlementRecords','PlanDescription') IS NULL
        ALTER TABLE EntitlementRecords ADD PlanDescription nvarchar(128) NULL;
        IF COL_LENGTH('EntitlementRecords','RenewalEnabled') IS NULL
        ALTER TABLE EntitlementRecords ADD RenewalEnabled bit NULL;
        IF COL_LENGTH('EntitlementRecords','BillableSeats') IS NULL
        ALTER TABLE EntitlementRecords ADD BillableSeats bigint NOT NULL CONSTRAINT DF_EntitlementRecords_BillableSeats DEFAULT 0;
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CustomerRecords_OwningLinkId')
        CREATE INDEX IX_CustomerRecords_OwningLinkId ON CustomerRecords(OwningLinkId);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CustomerRecords_IsDeleted')
        CREATE INDEX IX_CustomerRecords_IsDeleted ON CustomerRecords(IsDeleted);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_ResellerLinks_LastSyncedUtc')
        CREATE INDEX IX_ResellerLinks_LastSyncedUtc ON ResellerLinks(LastSyncedUtc);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_EntitlementRecords_OwningLinkId')
        CREATE INDEX IX_EntitlementRecords_OwningLinkId ON EntitlementRecords(OwningLinkId);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_EntitlementRecords_ProductId')
        CREATE INDEX IX_EntitlementRecords_ProductId ON EntitlementRecords(ProductId);
        """;
    await db.Database.ExecuteSqlRawAsync(sql);
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
        if (await db.KeyExistsAsync(ChannelNotificationFeed.RedisKey))
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

            await db.ListLeftPushAsync(ChannelNotificationFeed.RedisKey, JsonSerializer.Serialize(notification));
        }

        app.Logger.LogInformation("Seeded {Count} sample notifications into the development feed.", samples.Length);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Development notification seeding was skipped.");
    }
}
