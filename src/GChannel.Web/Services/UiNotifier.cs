using Blazorise;

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
/// Thin façade over Blazorise's <see cref="INotificationService"/> (toasts) and <see cref="IMessageService"/>
/// (confirm dialogs). Both are rendered by the providers in <c>MainLayout</c>. Registered scoped per circuit.
/// </summary>
public sealed class UiNotifier(INotificationService notifications, IMessageService messages)
{
    public Task Add(string message, Severity severity = Severity.Info) => severity switch
    {
        Severity.Success => notifications.Success(message),
        Severity.Warning => notifications.Warning(message),
        Severity.Error => notifications.Error(message),
        _ => notifications.Info(message)
    };

    /// <summary>Modal yes/cancel confirmation. Returns <c>true</c> only when the user picks the confirm button.</summary>
    public Task<bool> ConfirmAsync(string title, string message, string yesText = "OK", string cancelText = "Cancel") =>
        messages.Confirm(message, title, options =>
        {
            options.ConfirmButtonText = yesText;
            options.CancelButtonText = cancelText;
            options.ShowMessageIcon = false;
        });
}
