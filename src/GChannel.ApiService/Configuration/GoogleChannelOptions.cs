namespace GChannel.ApiService.Configuration;

/// <summary>Strongly-typed configuration for the Google Cloud Channel integration.</summary>
public sealed class GoogleChannelOptions
{
    public const string SectionName = "GoogleChannel";

    /// <summary>
    /// The reseller account resource name, e.g. "accounts/C01a2b3c".
    /// Found in the Partner Sales Console. Required for all Channel API calls.
    /// </summary>
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Application name reported to the Google API (telemetry/quota attribution).</summary>
    public string ApplicationName { get; set; } = "GChannel";

    /// <summary>Cache time-to-live (seconds) for idempotent lookups such as identity checks.</summary>
    public int CacheSeconds { get; set; } = 300;

    /// <summary>Normalised account name guaranteed to start with "accounts/".</summary>
    public string AccountName =>
        string.IsNullOrWhiteSpace(AccountId)
            ? string.Empty
            : AccountId.StartsWith("accounts/", StringComparison.OrdinalIgnoreCase)
                ? AccountId
                : $"accounts/{AccountId}";
}
