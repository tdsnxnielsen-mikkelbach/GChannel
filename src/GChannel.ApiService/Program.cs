using GChannel.ApiService;
using GChannel.ApiService.Services;
using GChannel.Core.DependencyInjection;
using GChannel.Core.GoogleApis;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence;
using JasperFx;
using Wolverine;
using Wolverine.Http;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GoogleApiExceptionHandler>();
builder.Services.AddHttpContextAccessor();

// Azure Database for PostgreSQL via Aspire ("gchanneldb"; Microsoft Entra ID token auth in Azure) hosting
// the Marten document store + event store and Wolverine's durable inbox/outbox (schema "wolverine_api" —
// the worker has its own). Schema changes are applied at startup.
builder.AddGChannelMarten(messageStorageSchema: "wolverine_api");

// Redis (Aspire connection "cache"): the shared Google-response cache (IDistributedCache). WithAzureAuthentication
// enables Microsoft Entra ID (managed identity) auth for Azure Managed Redis and is a no-op for the local container.
builder.AddRedisClientBuilder("cache")
    .WithAzureAuthentication()
    .WithDistributedCache();

// Shared options + Google/Ion integration services (also registered by the worker).
builder.Services.AddGChannelCore(builder.Configuration);

// The API calls the Channel API with the signed-in user's forwarded Google access token.
builder.Services.AddScoped<IGoogleChannelCredentialSource, RequestTokenCredentialSource>();
builder.Services.AddScoped<IGoogleChannelClient, GoogleChannelClient>();

// Wolverine: HTTP endpoints, the Service Bus transport (publishing CustomerChanged / OperationSubmitted /
// … through the Marten outbox) and the api-cache subscription (Redis eviction on read-model updates).
builder.UseWolverine(opts => opts.ConfigureGChannelMessaging("gchannel-api", MessagingRole.Api, typeof(ApiAssemblyMarker).Assembly, builder.Environment));
builder.Services.AddWolverineHttp();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Once the host is really serving — never during `codegen write` or other JasperFx commands.
    app.Lifetime.ApplicationStarted.Register(() => _ = DevelopmentSeeding.SeedNotificationsAsync(app));
}

app.MapDefaultEndpoints();

// Every /api/* route is a Wolverine.HTTP endpoint (see Endpoints/).
app.MapWolverineEndpoints(opts =>
{
    // Match Minimal API semantics: an unparseable query value is a 400, not a silent default.
    opts.RejectUnparseableQueryValues = true;
});

// Runs the host, or a JasperFx command such as `codegen write` (pre-generates the Wolverine code, see docs/deployment.md).
return await app.RunJasperFxCommands(args);
