using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace GChannel.Web.Services;

/// <summary>
/// Per-user onboarding progress (§9 phase 1). Holds only UI flags, so it is persisted in the browser
/// via <see cref="ProtectedLocalStorage"/> rather than a server table — the app uses
/// <c>EnsureCreated</c> with no EF migrations, and this state is low-sensitivity. A future upgrade can
/// move it to Redis keyed by the Google subject for cross-device persistence.
/// </summary>
public sealed class OnboardingState
{
    /// <summary>True once the user has seen and dismissed the first-run welcome card.</summary>
    public bool WelcomeDismissed { get; set; }

    /// <summary>True once the user hides the dashboard onboarding checklist.</summary>
    public bool ChecklistDismissed { get; set; }

    /// <summary>True once the user has finished (or closed) the guided product tour.</summary>
    public bool TourCompleted { get; set; }

    /// <summary>Keys of manually completed checklist steps (steps without a data signal).</summary>
    public List<string> CompletedSteps { get; set; } = [];
}

/// <summary>
/// Loads and saves <see cref="OnboardingState"/> in the browser's protected (data-protected) local
/// storage. All access is best-effort: storage is only reachable on the live interactive circuit, so
/// callers must read/write after the first render, and any failure degrades to a fresh state.
/// </summary>
public sealed class OnboardingStateService(ProtectedLocalStorage storage)
{
    private const string StorageKey = "gchannel.onboarding";

    public async Task<OnboardingState> LoadAsync()
    {
        try
        {
            var result = await storage.GetAsync<OnboardingState>(StorageKey);
            return result is { Success: true, Value: { } value } ? value : new OnboardingState();
        }
        catch
        {
            // Pre-render / interop unavailable / corrupt value — start fresh.
            return new OnboardingState();
        }
    }

    public async Task SaveAsync(OnboardingState state)
    {
        try
        {
            await storage.SetAsync(StorageKey, state);
        }
        catch
        {
            // Non-critical: losing a UI preference is acceptable.
        }
    }
}
