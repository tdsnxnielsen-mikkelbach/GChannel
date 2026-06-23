using System.Security.Claims;
using ApexCharts;
using GChannel.Web.Components;
using GChannel.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// UI: MudBlazor + ApexCharts.
builder.Services.AddMudServices();
builder.Services.AddApexCharts();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();

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
        options.Events.OnCreatingTicket = context =>
        {
            // Surface the access token as a claim so it is available inside the
            // interactive Server circuit (the cookie itself is data-protected).
            if (context.Identity is { } identity)
            {
                if (context.AccessToken is { } accessToken)
                {
                    identity.AddClaim(new Claim(GChannelApiClient.GoogleAccessTokenClaim, accessToken));
                }

                if (context.RefreshToken is { } refreshToken)
                {
                    identity.AddClaim(new Claim("google_refresh_token", refreshToken));
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
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
