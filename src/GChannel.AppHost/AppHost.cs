using Azure.Provisioning.RedisEnterprise;
using Azure.Provisioning.Sql;

var builder = DistributedApplication.CreateBuilder(args);

// Azure SQL Database — serverless General Purpose with auto-pause.
// Runs as a local SQL Server container during development. The container keeps
// a persistent data volume and a persistent lifetime so the database survives
// between debug sessions (no reseeding required).
var sql = builder.AddAzureSqlServer("sql")
    .RunAsContainer(container => container
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume("gchannel-sql-data"));

var database = sql.AddDatabase("gchanneldb");

sql.ConfigureInfrastructure(infra =>
{
    var db = infra.GetProvisionableResources().OfType<SqlDatabase>().Single();
    db.Sku = new SqlSku
    {
        Name = "GP_S_Gen5_2",
        Tier = "GeneralPurpose",
        Family = "Gen5",
        Capacity = 2
    };
    db.MinCapacity = 0.5;
    db.AutoPauseDelay = 60; // pause after 60 minutes of inactivity
    // The already-provisioned free-limit DB uses BillOverUsage; Azure forbids changing this in place,
    // so pin it to match the deployed resource (the current Aspire default would try to set AutoPause).
    db.UseFreeLimit = true;
    db.FreeLimitExhaustionBehavior = FreeLimitExhaustionBehavior.BillOverUsage;
});

// Azure Managed Redis — entry-level Balanced B0 tier ("managed redis, basic").
// Runs as a local Redis container during development. A persistent data volume
// plus RDB snapshotting and a persistent lifetime keep the cache warm between
// debug sessions.
var cache = builder.AddAzureManagedRedis("cache")
    .RunAsContainer(container => container
        .WithLifetime(ContainerLifetime.Persistent)
        .WithDataVolume("gchannel-redis-data")
        .WithPersistence());

cache.ConfigureInfrastructure(infra =>
{
    var cluster = infra.GetProvisionableResources().OfType<RedisEnterpriseCluster>().Single();
    cluster.Sku = new RedisEnterpriseSku
    {
        Name = RedisEnterpriseSkuName.BalancedB0
    };
});

// Azure Key Vault — central, secure store for application secrets (best practice).
// The OAuth client secret is persisted here and surfaced to the app as a Key Vault
// reference, so the literal value never appears in the deployment manifest or as
// plain-text Container Apps configuration.
// Key Vault has no local emulator, so it is only provisioned when publishing.
var keyVault = builder.AddAzureKeyVault("secrets");

// Configuration supplied at deploy time (azd prompts for these once, or `azd env set`).
var googleClientId = builder.AddParameter("GoogleClientId");
var googleClientSecretParam = builder.AddParameter("GoogleClientSecret", secret: true);
var googleChannelAccountId = builder.AddParameter("GoogleChannelAccountId");

// Optional background dashboard refresh (service account + domain-wide delegation). Leave the
// service-account key empty / refresh seconds at 0 to keep it disabled; set all three to enable.
var googleChannelImpersonateUser = builder.AddParameter("GoogleChannelImpersonateUser");
var googleChannelBackgroundRefreshSeconds = builder.AddParameter("GoogleChannelBackgroundRefreshSeconds");
var googleChannelServiceAccountKeyParam = builder.AddParameter("GoogleChannelServiceAccountKeyJson", secret: true);

// Client-side pacing (requests/minute) so the dashboard aggregation stays under the Channel API's
// tight per-minute quotas (typically 24/min each for ListEntitlements and ListCustomers). Sourced
// from config.json; see GoogleChannelOptions for the defaults and semantics.
var googleChannelDashboardRequestsPerMinute = builder.AddParameter("GoogleChannelDashboardRequestsPerMinute");
var googleChannelDashboardCustomerListRequestsPerMinute = builder.AddParameter("GoogleChannelDashboardCustomerListRequestsPerMinute");

// Optional §10 persistent read-model: when enabled (and the background service-account credential is
// set), a worker incrementally materialises the estate into SQL so the dashboard reads durable, indexed
// aggregates instead of a live per-reseller fan-out. LinksPerCycle is the per-cycle quota budget.
var googleChannelUseReadModel = builder.AddParameter("GoogleChannelUseReadModel");
var googleChannelReadModelLinksPerCycle = builder.AddParameter("GoogleChannelReadModelLinksPerCycle");

// Optional Pub/Sub notification subscriber (§7). Point these at the subscription you created in your
// own Google Cloud project against the Channel topic; leave blank to keep the subscriber disabled.
// Authentication prefers Workload Identity Federation (key-less, recommended) and falls back to the
// service-account key above. The WIF credential config is the external_account JSON from
// `gcloud iam workload-identity-pools create-cred-config` — it holds no private key, so it is a plain
// (non-secret) parameter; the Azure managed identity supplies the actual identity at run time.
var googleChannelPubSubProjectId = builder.AddParameter("GoogleChannelPubSubProjectId");
var googleChannelPubSubSubscriptionId = builder.AddParameter("GoogleChannelPubSubSubscriptionId");
var googleChannelWorkloadIdentityCredentialJson = builder.AddParameter("GoogleChannelWorkloadIdentityCredentialJson");

// §15 Cloud Billing → BigQuery detailed cost-export sync. All optional and disabled by default
// (BigQueryEnabled=false), so the feature stays a no-op until an operator sets these via `azd env set`.
// Passed to BOTH the worker (runs the sync) and the API (its spend endpoints report configured/unconfigured).
var googleBillingBigQueryEnabled = builder.AddParameter("GoogleBillingBigQueryEnabled");
var googleBillingBigQueryProjectId = builder.AddParameter("GoogleBillingBigQueryProjectId");
var googleBillingExportDataset = builder.AddParameter("GoogleBillingExportDataset");
var googleBillingExportTable = builder.AddParameter("GoogleBillingExportTable");
var googleBillingExcludeBillingAccountIds = builder.AddParameter("GoogleBillingExcludeBillingAccountIds");

// Ion Gateway (TD SYNNEX StreamOne) enrichment. Disabled by default; the API key is a secret persisted in
// Key Vault (like the OAuth secret and SA key), so it never appears in the manifest or plain config.
var ionGatewayEnabled = builder.AddParameter("IonGatewayEnabled");
var ionGatewayBaseUrl = builder.AddParameter("IonGatewayBaseUrl");
var ionGatewayApiKeyParam = builder.AddParameter("IonGatewayApiKey", secret: true);

// Back-end services container app (internal): owns SQL, Redis and the Google Channel API.
var apiService = builder.AddProject<Projects.GChannel_ApiService>("apiservice")
    .WithReference(database)
    .WithReference(cache)
    .WithEnvironment("GoogleChannel__AccountId", googleChannelAccountId)
    .WithEnvironment("GoogleChannel__ImpersonateUser", googleChannelImpersonateUser)
    .WithEnvironment("GoogleChannel__BackgroundRefreshSeconds", googleChannelBackgroundRefreshSeconds)
    .WithEnvironment("GoogleChannel__DashboardRequestsPerMinute", googleChannelDashboardRequestsPerMinute)
    .WithEnvironment("GoogleChannel__DashboardCustomerListRequestsPerMinute", googleChannelDashboardCustomerListRequestsPerMinute)
    .WithEnvironment("GoogleChannel__UseReadModel", googleChannelUseReadModel)
    .WithEnvironment("GoogleChannel__ReadModelLinksPerCycle", googleChannelReadModelLinksPerCycle)
    .WithEnvironment("GoogleChannel__PubSubProjectId", googleChannelPubSubProjectId)
    .WithEnvironment("GoogleChannel__PubSubSubscriptionId", googleChannelPubSubSubscriptionId)
    .WithEnvironment("GoogleChannel__WorkloadIdentityCredentialJson", googleChannelWorkloadIdentityCredentialJson)
    .WithEnvironment("GoogleBilling__BigQueryEnabled", googleBillingBigQueryEnabled)
    .WithEnvironment("GoogleBilling__BigQueryProjectId", googleBillingBigQueryProjectId)
    .WithEnvironment("GoogleBilling__BillingExportDataset", googleBillingExportDataset)
    .WithEnvironment("GoogleBilling__BillingExportTable", googleBillingExportTable)
    .WithEnvironment("GoogleBilling__ExcludeBillingAccountIds", googleBillingExcludeBillingAccountIds)
    .WithEnvironment("IonGateway__Enabled", ionGatewayEnabled)
    .WithEnvironment("IonGateway__BaseUrl", ionGatewayBaseUrl)
    .WaitFor(database)
    .WaitFor(cache);

// Background worker container app (internal, no HTTP): the dashboard refresh, Pub/Sub subscriber and
// read-model sync hosted services, extracted from the API so they scale on their own axis. Pinned to a
// single replica via WithReplicas(1) — Redis single-flight locks keep extra replicas idle, and these
// jobs are scheduled/pulled, so there is no benefit to scaling out.
var worker = builder.AddProject<Projects.GChannel_Worker>("worker")
    .WithReference(database)
    .WithReference(cache)
    .WithEnvironment("GoogleChannel__AccountId", googleChannelAccountId)
    .WithEnvironment("GoogleChannel__ImpersonateUser", googleChannelImpersonateUser)
    .WithEnvironment("GoogleChannel__BackgroundRefreshSeconds", googleChannelBackgroundRefreshSeconds)
    .WithEnvironment("GoogleChannel__DashboardRequestsPerMinute", googleChannelDashboardRequestsPerMinute)
    .WithEnvironment("GoogleChannel__DashboardCustomerListRequestsPerMinute", googleChannelDashboardCustomerListRequestsPerMinute)
    .WithEnvironment("GoogleChannel__UseReadModel", googleChannelUseReadModel)
    .WithEnvironment("GoogleChannel__ReadModelLinksPerCycle", googleChannelReadModelLinksPerCycle)
    .WithEnvironment("GoogleChannel__PubSubProjectId", googleChannelPubSubProjectId)
    .WithEnvironment("GoogleChannel__PubSubSubscriptionId", googleChannelPubSubSubscriptionId)
    .WithEnvironment("GoogleChannel__WorkloadIdentityCredentialJson", googleChannelWorkloadIdentityCredentialJson)
    .WithEnvironment("GoogleBilling__BigQueryEnabled", googleBillingBigQueryEnabled)
    .WithEnvironment("GoogleBilling__BigQueryProjectId", googleBillingBigQueryProjectId)
    .WithEnvironment("GoogleBilling__BillingExportDataset", googleBillingExportDataset)
    .WithEnvironment("GoogleBilling__BillingExportTable", googleBillingExportTable)
    .WithEnvironment("GoogleBilling__ExcludeBillingAccountIds", googleBillingExcludeBillingAccountIds)
    .WithEnvironment("IonGateway__Enabled", ionGatewayEnabled)
    .WithEnvironment("IonGateway__BaseUrl", ionGatewayBaseUrl)
    .WithReplicas(1)
    .WaitFor(database)
    .WaitFor(cache);

// Blazor front end container app (external): the dashboard users sign in to.
var webfrontend = builder.AddProject<Projects.GChannel_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithReference(apiService)
    .WithEnvironment("Authentication__Google__ClientId", googleClientId)
    .WaitFor(apiService);

if (builder.ExecutionContext.IsPublishMode)
{
    // In Azure, persist the OAuth client secret in Key Vault and surface it to the
    // app as a Key Vault reference (resolved via the app's managed identity), so the
    // literal value never appears in the deployment manifest or app configuration.
    keyVault.AddSecret("google-client-secret", googleClientSecretParam);
    var googleClientSecret = keyVault.GetSecret("google-client-secret");

    webfrontend
        .WithReference(keyVault)
        .WithEnvironment("Authentication__Google__ClientSecret", googleClientSecret);

    // Same treatment for the service-account key used by the background dashboard refresh.
    keyVault.AddSecret("google-channel-sa-key", googleChannelServiceAccountKeyParam);
    var googleChannelServiceAccountKey = keyVault.GetSecret("google-channel-sa-key");

    apiService
        .WithReference(keyVault)
        .WithEnvironment("GoogleChannel__ServiceAccountKeyJson", googleChannelServiceAccountKey);

    // The worker performs the actual service-account work (dashboard refresh + read-model sync), so it
    // also needs the key from Key Vault.
    worker
        .WithReference(keyVault)
        .WithEnvironment("GoogleChannel__ServiceAccountKeyJson", googleChannelServiceAccountKey);

    // Ion Gateway API key: persist in Key Vault and surface to both services (the projector runs in both).
    keyVault.AddSecret("ion-gateway-api-key", ionGatewayApiKeyParam);
    var ionGatewayApiKey = keyVault.GetSecret("ion-gateway-api-key");
    apiService
        .WithReference(keyVault)
        .WithEnvironment("IonGateway__ApiKey", ionGatewayApiKey);
    worker
        .WithReference(keyVault)
        .WithEnvironment("IonGateway__ApiKey", ionGatewayApiKey);
}
else
{
    // Local development: Key Vault cannot be provisioned, so inject the secret
    // parameter values (sourced from user secrets) directly.
    webfrontend.WithEnvironment("Authentication__Google__ClientSecret", googleClientSecretParam);
    apiService.WithEnvironment("GoogleChannel__ServiceAccountKeyJson", googleChannelServiceAccountKeyParam);
    worker.WithEnvironment("GoogleChannel__ServiceAccountKeyJson", googleChannelServiceAccountKeyParam);
    apiService.WithEnvironment("IonGateway__ApiKey", ionGatewayApiKeyParam);
    worker.WithEnvironment("IonGateway__ApiKey", ionGatewayApiKeyParam);
}

builder.Build().Run();
