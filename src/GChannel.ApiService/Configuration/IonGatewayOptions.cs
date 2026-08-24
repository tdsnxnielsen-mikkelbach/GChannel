namespace GChannel.ApiService.Configuration;

/// <summary>
/// Configuration for the Ion Gateway (TD SYNNEX StreamOne enrichment hub). GChannel already holds Google
/// wholesale <b>list</b> price on each entitlement; the gateway adds the real transacted <b>cost/margin</b>
/// from Ion. Authenticated with an <c>X-Api-Key</c> (stored in Key Vault, never in source). Everything is a
/// no-op unless <see cref="Enabled"/> plus <see cref="BaseUrl"/> and <see cref="ApiKey"/> are set.
/// </summary>
public sealed class IonGatewayOptions
{
    public const string SectionName = "IonGateway";

    /// <summary>Feature flag: when false the Ion enrichment is skipped and Ion fields stay null.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gateway base URL, e.g. <c>https://gateway.greentree-40a949e3.swedencentral.azurecontainerapps.io</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>The <c>X-Api-Key</c> (format <c>{keyId}.{secret}</c>). Sourced from Key Vault / user-secrets.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>True when the gateway can be called.</summary>
    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
