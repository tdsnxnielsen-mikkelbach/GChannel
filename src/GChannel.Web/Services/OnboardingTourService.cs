using Microsoft.JSInterop;

namespace GChannel.Web.Services;

/// <summary>
/// Drives the §9 phase 2 guided product tour. Wraps the <c>onboarding.js</c> module (Driver.js) and
/// persists completion via <see cref="OnboardingStateService"/>. Registered scoped so the whole circuit
/// (app bar "Restart tour", welcome card "Take a tour") shares one JS module + .NET reference.
/// </summary>
public sealed class OnboardingTourService(IJSRuntime js, OnboardingStateService state) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private DotNetObjectReference<OnboardingTourService>? _selfRef;

    // The tour walks the always-present app chrome (nav drawer + app bar), so it can launch from any
    // page without cross-page navigation. Steps targeting a missing element are skipped client-side.
    private static readonly TourStep[] Steps =
    [
        new("[data-onboarding=\"nav-dashboard\"]", "Dashboard", "Your reseller overview — customers, active SKUs and recent activity at a glance."),
        new("[data-onboarding=\"nav-accounts\"]", "Accounts", "Verify a customer's domain with the Cloud Identity check before you onboard them."),
        new("[data-onboarding=\"nav-catalog\"]", "Catalog", "Browse products, offers and SKU groups available to resell."),
        new("[data-onboarding=\"nav-customers\"]", "Customers", "Create customers and manage their entitlements (purchase, suspend, transfer)."),
        new("[data-onboarding=\"nav-partners\"]", "Channel partners", "Invite and manage channel partner links for two-tier resale."),
        new("[data-onboarding=\"nav-eventing\"]", "Eventing", "Track long-running operations and live Pub/Sub notifications."),
        new("[data-onboarding=\"appbar-theme\"]", "Light & dark", "Switch the theme to suit your environment."),
        new("[data-onboarding=\"appbar-help\"]", "Restart anytime", "Replay this tour whenever you like from here."),
    ];

    public async Task StartAsync()
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/onboarding.js");
        _selfRef ??= DotNetObjectReference.Create(this);
        await _module.InvokeVoidAsync("startTour", _selfRef, Steps);
    }

    [JSInvokable]
    public async Task OnTourCompleted()
    {
        var current = await state.LoadAsync();
        if (current.TourCompleted)
        {
            return;
        }

        current.TourCompleted = true;
        await state.SaveAsync(current);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch
            {
                // Circuit already gone — nothing to release.
            }
        }

        _selfRef?.Dispose();
    }

    private sealed record TourStep(string Element, string Title, string Description);
}
