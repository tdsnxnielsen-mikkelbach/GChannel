using System.Security.Claims;
using ApexCharts;
using GChannel.Web.Components;
using GChannel.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.HttpOverrides;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// The Azure Container Apps ingress terminates TLS and forwards the request to the container over
// plain HTTP, carrying the original scheme/client in X-Forwarded-Proto / X-Forwarded-For. Honour
// those so the app treats the request as HTTPS (secure auth cookies + correct OAuth redirect URIs).
// The ingress address isn't known ahead of time, so don't restrict by known proxy/network.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// UI: MudBlazor + ApexCharts.
builder.Services.AddMudServices();
builder.Services.AddApexCharts();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

// Caches silently-refreshed Google access tokens per user.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<GoogleTokenProvider>();

// Google sign-in. The reseller signs in with their Google account; we request the
// Channel API scope and keep the access/refresh tokens so the API service can act on
// the user's behalf.
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
    })
    .AddCookie()
    .AddGoogle(options =>
    {
        options.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? string.Empty;
        options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? string.Empty;
        options.SaveTokens = true;
        options.AccessType = "offline";
        options.Scope.Add("https://www.googleapis.com/auth/apps.order");

        // Google only issues a refresh token on the *first* consent unless we explicitly force the
        // consent prompt. Without one, the access token captured at sign-in expires after ~1 hour and
        // GoogleTokenProvider has nothing to refresh with, so it forwards the stale token and the
        // Channel API rejects it with 401. Forcing "consent" (together with offline access above)
        // guarantees a refresh token so tokens can be renewed silently for the life of the session.
        options.AdditionalAuthorizationParameters["prompt"] = "consent";
        options.Events.OnCreatingTicket = context =>
        {
            // Surface the access token as a claim so it is available inside the
            // interactive Server circuit (the cookie itself is data-protected).
            if (context.Identity is { } identity)
            {
                if (context.AccessToken is { } accessToken)
                {
                    identity.AddClaim(new Claim(GoogleTokenProvider.AccessTokenClaim, accessToken));
                }

                if (context.RefreshToken is { } refreshToken)
                {
                    identity.AddClaim(new Claim(GoogleTokenProvider.RefreshTokenClaim, refreshToken));
                }

                // Record when the access token expires so it can be refreshed silently later.
                if (int.TryParse(context.TokenResponse.ExpiresIn, out var expiresInSeconds))
                {
                    identity.AddClaim(new Claim(
                        GoogleTokenProvider.ExpiresAtClaim,
                        DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToString("o")));
                }
            }

            return Task.CompletedTask;
        };
    });

// Typed client to the API service (resolved via Aspire service discovery).
builder.Services.AddHttpClient<GChannelApiClient>(client =>
{
    client.BaseAddress = new Uri("https+http://apiservice");
});

// Per-user onboarding progress (§9 phase 1), persisted in the browser's protected local storage.
builder.Services.AddScoped<OnboardingStateService>();

var app = builder.Build();

// Apply the forwarded scheme/client from the ingress before anything that depends on the request
// scheme (HSTS, HTTPS redirect, secure cookies, OAuth redirect-URI generation).
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

// Minimal auth endpoints used by the UI.
app.MapGet("/account/login", (string? returnUrl) =>
    Results.Challenge(
        new() { RedirectUri = returnUrl ?? "/" },
        [GoogleDefaults.AuthenticationScheme]));

app.MapPost("/account/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/");
});

app.MapDefaultEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
