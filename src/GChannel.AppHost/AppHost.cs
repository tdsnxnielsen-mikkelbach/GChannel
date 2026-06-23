using Azure.Provisioning.RedisEnterprise;
using Azure.Provisioning.Sql;

var builder = DistributedApplication.CreateBuilder(args);

// Azure SQL Database — serverless General Purpose with auto-pause.
// Runs as a local SQL Server container during development.
var sql = builder.AddAzureSqlServer("sql")
    .RunAsContainer();

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
});

// Azure Managed Redis — entry-level Balanced B0 tier ("managed redis, basic").
// Runs as a local Redis container during development.
var cache = builder.AddAzureManagedRedis("cache")
    .RunAsContainer();

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

// Configuration supplied at deploy time (azd prompts for these once).
var googleClientId = builder.AddParameter("GoogleClientId");
var googleClientSecretParam = builder.AddParameter("GoogleClientSecret", secret: true);
var googleChannelAccountId = builder.AddParameter("GoogleChannelAccountId");

// Back-end services container app (internal): owns SQL, Redis and the Google Channel API.
var apiService = builder.AddProject<Projects.GChannel_ApiService>("apiservice")
    .WithReference(database)
    .WithReference(cache)
    .WithEnvironment("GoogleChannel__AccountId", googleChannelAccountId)
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
}
else
{
    // Local development: Key Vault cannot be provisioned, so inject the secret
    // parameter value (sourced from user secrets) directly.
    webfrontend.WithEnvironment("Authentication__Google__ClientSecret", googleClientSecretParam);
}

builder.Build().Run();
