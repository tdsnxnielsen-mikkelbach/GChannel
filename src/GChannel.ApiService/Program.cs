using GChannel.ApiService.Data;
using GChannel.ApiService.Endpoints;
using GChannel.ApiService.Services;
using GChannel.Core.Configuration;
using GChannel.Core.DependencyInjection;
using GChannel.Core.GoogleApis;
using GChannel.Core.Ion;
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
// Azure Managed Redis, which is provisioned with access keys disabled; it is a no-op for the local
// RunAsContainer cache (which uses a password). The IConnectionMultiplexer is used by the background
// dashboard refresher to take a cluster-wide lock so only one replica recomputes per interval.
builder.AddRedisClientBuilder("cache")
    .WithAzureAuthentication()
    .WithDistributedCache();

// Shared options + Google/Ion integration services (also registered by the worker).
builder.Services.AddGChannelCore(builder.Configuration);

builder.Services.AddScoped<IGoogleChannelCredentialSource, RequestTokenCredentialSource>();
builder.Services.AddScoped<IGoogleChannelClient, GoogleChannelClient>();

builder.Services.AddSingleton<MemberIdentityResolver>();

// §10 read-model write-through: mutation endpoints upsert the one changed customer row into the
// read-model immediately after a successful Channel API call, so the estate/customers lists reflect a
// create/import/update/delete without waiting for the next background sync cycle. Stateless (takes the
// DbContext per call), so a singleton is fine.
builder.Services.AddSingleton<ReadModelProjector>();

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
app.MapBillingEndpoints();
app.MapBillingPostureEndpoints();
app.MapSellerSpendEndpoints();

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
            IsResellerSelf bit NOT NULL CONSTRAINT DF_CustomerRecords_IsResellerSelf DEFAULT 0,
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
            BillingAccount nvarchar(128) NULL,
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
            IsResellerSelf bit NOT NULL CONSTRAINT DF_EntitlementRecords_IsResellerSelf DEFAULT 0,
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
        IF COL_LENGTH('CustomerRecords','IsResellerSelf') IS NULL
        ALTER TABLE CustomerRecords ADD IsResellerSelf bit NOT NULL CONSTRAINT DF_CustomerRecords_IsResellerSelf DEFAULT 0;
        IF COL_LENGTH('CustomerRecords','CloudIdentityDomain') IS NULL
        ALTER TABLE CustomerRecords ADD CloudIdentityDomain nvarchar(255) NULL;
        IF COL_LENGTH('EntitlementRecords','IsResellerSelf') IS NULL
        ALTER TABLE EntitlementRecords ADD IsResellerSelf bit NOT NULL CONSTRAINT DF_EntitlementRecords_IsResellerSelf DEFAULT 0;
        IF COL_LENGTH('EntitlementRecords','BillingAccount') IS NULL
        ALTER TABLE EntitlementRecords ADD BillingAccount nvarchar(128) NULL;
        IF COL_LENGTH('EntitlementRecords','ProvisioningId') IS NULL
        ALTER TABLE EntitlementRecords ADD ProvisioningId nvarchar(128) NULL;
        IF COL_LENGTH('EntitlementRecords','IonCostPerSeat') IS NULL
        ALTER TABLE EntitlementRecords ADD IonCostPerSeat decimal(18,6) NULL;
        IF COL_LENGTH('EntitlementRecords','IonMarginPercent') IS NULL
        ALTER TABLE EntitlementRecords ADD IonMarginPercent decimal(9,4) NULL;
        IF COL_LENGTH('EntitlementRecords','IonCurrency') IS NULL
        ALTER TABLE EntitlementRecords ADD IonCurrency nvarchar(8) NULL;
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
        IF OBJECT_ID('CustomerSpend', 'U') IS NULL
        CREATE TABLE CustomerSpend (
            BillingSubAccountId nvarchar(64) NOT NULL,
            InvoiceMonth nvarchar(6) NOT NULL,
            Currency nvarchar(8) NOT NULL,
            CustomerId nvarchar(128) NULL,
            OwningLinkId nvarchar(128) NULL,
            Cost decimal(18,6) NOT NULL,
            GrossCost decimal(18,6) NOT NULL,
            Credits decimal(18,6) NOT NULL,
            LastSyncedUtc datetimeoffset NOT NULL,
            CONSTRAINT PK_CustomerSpend PRIMARY KEY (BillingSubAccountId, InvoiceMonth, Currency)
        );
        IF OBJECT_ID('SpendDaily', 'U') IS NULL
        CREATE TABLE SpendDaily (
            BillingSubAccountId nvarchar(64) NOT NULL,
            Day date NOT NULL,
            Currency nvarchar(8) NOT NULL,
            CustomerId nvarchar(128) NULL,
            OwningLinkId nvarchar(128) NULL,
            Cost decimal(18,6) NOT NULL,
            LastSyncedUtc datetimeoffset NOT NULL,
            CONSTRAINT PK_SpendDaily PRIMARY KEY (BillingSubAccountId, Day, Currency)
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CustomerSpend_CustomerId')
        CREATE INDEX IX_CustomerSpend_CustomerId ON CustomerSpend(CustomerId);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CustomerSpend_OwningLinkId')
        CREATE INDEX IX_CustomerSpend_OwningLinkId ON CustomerSpend(OwningLinkId);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CustomerSpend_InvoiceMonth')
        CREATE INDEX IX_CustomerSpend_InvoiceMonth ON CustomerSpend(InvoiceMonth);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SpendDaily_CustomerId')
        CREATE INDEX IX_SpendDaily_CustomerId ON SpendDaily(CustomerId);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_SpendDaily_Day')
        CREATE INDEX IX_SpendDaily_Day ON SpendDaily(Day);
        -- Billing IAM security posture (nightly scan by the worker's BillingPostureSyncService).
        IF OBJECT_ID('BillingPostureAccounts', 'U') IS NULL
        CREATE TABLE BillingPostureAccounts (
            BillingAccountId nvarchar(64) NOT NULL,
            DisplayName nvarchar(256) NULL,
            MasterBillingAccountId nvarchar(64) NULL,
            [Open] bit NOT NULL,
            CustomerId nvarchar(128) NULL,
            Error nvarchar(1024) NULL,
            LastSyncedUtc datetimeoffset NOT NULL,
            CONSTRAINT PK_BillingPostureAccounts PRIMARY KEY (BillingAccountId)
        );
        IF OBJECT_ID('BillingPostureMembers', 'U') IS NULL
        CREATE TABLE BillingPostureMembers (
            BillingAccountId nvarchar(64) NOT NULL,
            Email nvarchar(320) NOT NULL,
            Role nvarchar(128) NOT NULL,
            Inherited bit NOT NULL,
            MemberType nvarchar(32) NOT NULL,
            RoleDisplayName nvarchar(128) NOT NULL,
            AccountKind int NOT NULL,
            WorkspaceCustomerId nvarchar(64) NULL,
            SecurityVisible bit NOT NULL,
            TwoSvEnrolled bit NULL,
            TwoSvEnforced bit NULL,
            AdvancedProtection int NOT NULL,
            AdvancedProtectionAsOf datetimeoffset NULL,
            SecurityKeys int NULL,
            Passkeys int NULL,
            KeysAsOf date NULL,
            IsAdmin bit NULL,
            Suspended bit NULL,
            LastSyncedUtc datetimeoffset NOT NULL,
            CONSTRAINT PK_BillingPostureMembers PRIMARY KEY (BillingAccountId, Email, Role, Inherited)
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_BillingPostureMembers_Email')
        CREATE INDEX IX_BillingPostureMembers_Email ON BillingPostureMembers(Email);
        -- Native Google vs third-party (Marketplace / partner) spend split from the export.
        IF OBJECT_ID('CustomerSpendBySeller', 'U') IS NULL
        CREATE TABLE CustomerSpendBySeller (
            BillingSubAccountId nvarchar(64) NOT NULL,
            InvoiceMonth nvarchar(6) NOT NULL,
            Currency nvarchar(8) NOT NULL,
            TransactionType nvarchar(32) NOT NULL,
            SellerKey nvarchar(256) NOT NULL,
            ServiceKey nvarchar(256) NOT NULL,
            SellerName nvarchar(256) NULL,
            ServiceDescription nvarchar(256) NULL,
            CustomerId nvarchar(128) NULL,
            OwningLinkId nvarchar(128) NULL,
            Cost decimal(18,6) NOT NULL,
            GrossCost decimal(18,6) NOT NULL,
            Credits decimal(18,6) NOT NULL,
            LastSyncedUtc datetimeoffset NOT NULL,
            CONSTRAINT PK_CustomerSpendBySeller PRIMARY KEY (BillingSubAccountId, InvoiceMonth, Currency, TransactionType, SellerKey, ServiceKey)
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CustomerSpendBySeller_CustomerId')
        CREATE INDEX IX_CustomerSpendBySeller_CustomerId ON CustomerSpendBySeller(CustomerId);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_CustomerSpendBySeller_InvoiceMonth')
        CREATE INDEX IX_CustomerSpendBySeller_InvoiceMonth ON CustomerSpendBySeller(InvoiceMonth);
        -- Marketplace orders (Consumer Procurement API), scanned nightly.
        IF OBJECT_ID('MarketplaceOrders', 'U') IS NULL
        CREATE TABLE MarketplaceOrders (
            Name nvarchar(256) NOT NULL,
            BillingAccountId nvarchar(64) NOT NULL,
            CustomerId nvarchar(128) NULL,
            DisplayName nvarchar(256) NULL,
            Provider nvarchar(128) NULL,
            ProductsCsv nvarchar(2048) NULL,
            OffersCsv nvarchar(2048) NULL,
            CreateTime datetimeoffset NULL,
            UpdateTime datetimeoffset NULL,
            Active bit NOT NULL,
            LastSyncedUtc datetimeoffset NOT NULL,
            CONSTRAINT PK_MarketplaceOrders PRIMARY KEY (Name)
        );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MarketplaceOrders_BillingAccountId')
        CREATE INDEX IX_MarketplaceOrders_BillingAccountId ON MarketplaceOrders(BillingAccountId);
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='IX_MarketplaceOrders_CustomerId')
        CREATE INDEX IX_MarketplaceOrders_CustomerId ON MarketplaceOrders(CustomerId);
        -- One-time backfill so estate value shows immediately after the BillableSeats column is added,
        -- instead of reading 0 everywhere until every entitlement has been re-synced. For COMMITTED
        -- entitlements the stored Seats already equals num_units (SeatsOf only falls back to a flexible
        -- plan's max_units CAP when num_units is absent, and those flexible plans carry no CommitmentEndTime),
        -- so copying Seats is exactly the billable seat count. The Seats <= 100000 guard is belt-and-braces
        -- against an absurd flexible cap slipping through. A SyncCursors marker makes it run exactly once;
        -- the worker keeps BillableSeats current from then on.
        IF NOT EXISTS (SELECT 1 FROM SyncCursors WHERE Scope = 'billableseats-backfill')
        BEGIN
            UPDATE EntitlementRecords SET BillableSeats = Seats
                WHERE BillableSeats = 0 AND Seats > 0 AND Seats <= 100000 AND CommitmentEndTime IS NOT NULL;
            INSERT INTO SyncCursors (Scope, LastCycleUtc) VALUES ('billableseats-backfill', SYSDATETIMEOFFSET());
        END
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
