using GChannel.ApiService.Configuration;
using GChannel.ApiService.Data;
using GChannel.ApiService.Endpoints;
using GChannel.ApiService.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();

// Azure SQL (serverless) via Aspire — connection name must match AppHost ("gchanneldb").
builder.AddSqlServerDbContext<GChannelDbContext>("gchanneldb");

// Redis distributed cache via Aspire — connection name must match AppHost ("cache").
builder.AddRedisDistributedCache("cache");

builder.Services
    .AddOptions<GoogleChannelOptions>()
    .Bind(builder.Configuration.GetSection(GoogleChannelOptions.SectionName));

builder.Services.AddScoped<IGoogleChannelClient, GoogleChannelClient>();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

await EnsureDatabaseAsync(app);

app.MapDefaultEndpoints();
app.MapAccountsEndpoints();

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
