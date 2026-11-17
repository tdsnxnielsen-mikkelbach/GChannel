using Microsoft.AspNetCore.Components;
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
public sealed class UiNotifier(NotificationService notifications, DialogService dialogs, NavigationManager navigation)
{
    public Task Add(string message, Severity severity = Severity.Info)
    {
        notifications.Notify(new NotificationMessage
        {
            Severity = Map(severity),
            Summary = message,
            Duration = severity == Severity.Error ? 8000 : 4000,
            CloseOnClick = true
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// Toast for an action that returned a Google long-running operation: names the operation and opens it
    /// on the Operations page when clicked, so its outcome (and any error Google reports later) is one click away.
    /// </summary>
    public Task AddOperation(string message, string? operationName, Severity severity = Severity.Info)
    {
        var operationId = OperationIdOf(operationName);
        if (operationId is null)
        {
            return Add(message, severity);
        }

        notifications.Notify(new NotificationMessage
        {
            Severity = Map(severity),
            Summary = message,
            Detail = $"Operation {operationId} — click to track it.",
            Duration = 10000,
            CloseOnClick = true,
            Click = _ => navigation.NavigateTo(OperationLink(operationId))
        });
        return Task.CompletedTask;
    }

    /// <summary>The Operations page URL that tracks an operation (id or full "operations/…" name).</summary>
    public static string OperationLink(string operationIdOrName) =>
        $"/operations?operation={Uri.EscapeDataString(OperationIdOf(operationIdOrName) ?? operationIdOrName)}";

    private static string? OperationIdOf(string? operationName) =>
        string.IsNullOrWhiteSpace(operationName) ? null : operationName.Split('/')[^1];

    private static NotificationSeverity Map(Severity severity) => severity switch
    {
        Severity.Success => NotificationSeverity.Success,
        Severity.Warning => NotificationSeverity.Warning,
        Severity.Error => NotificationSeverity.Error,
        _ => NotificationSeverity.Info
    };

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