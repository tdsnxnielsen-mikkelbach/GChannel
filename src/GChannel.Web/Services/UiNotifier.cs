using Radzen;

namespace GChannel.Web.Services;

/// <summary>Toast severity. Mirrors the levels pages used with the previous snackbar so call sites stay terse.</summary>
public enum Severity
{
    Info,
    Success,
    Warning,
    Error
}

/// <summary>
/// Thin façade over Radzen's <see cref="NotificationService"/> (toasts) and <see cref="DialogService"/>
/// (confirm dialogs). Both are hosted by the <c>RadzenComponents</c> element in <c>MainLayout</c>. Registered scoped per circuit.
/// </summary>
public sealed class UiNotifier(NotificationService notifications, DialogService dialogs)
{
    public Task Add(string message, Severity severity = Severity.Info)
    {
        notifications.Notify(new NotificationMessage
        {
            Severity = severity switch
            {
                Severity.Success => NotificationSeverity.Success,
                Severity.Warning => NotificationSeverity.Warning,
                Severity.Error => NotificationSeverity.Error,
                _ => NotificationSeverity.Info
            },
            Summary = message,
            Duration = severity == Severity.Error ? 8000 : 4000,
            CloseOnClick = true
        });
        return Task.CompletedTask;
    }

    /// <summary>Modal yes/cancel confirmation. Returns <c>true</c> only when the user picks the confirm button.</summary>
    public async Task<bool> ConfirmAsync(string title, string message, string yesText = "OK", string cancelText = "Cancel")
    {
        var result = await dialogs.Confirm(message, title, new ConfirmOptions
        {
            OkButtonText = yesText,
            CancelButtonText = cancelText,
            CloseDialogOnOverlayClick = true
        });
        return result == true;
    }
}