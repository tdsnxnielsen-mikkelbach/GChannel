namespace GChannel.Shared.Contracts;

/// <summary>
/// Well-known route fragments shared between the Web client and the API service.
/// Keeps the abstraction in one place so UI code never references Google REST paths.
/// </summary>
public static class ApiRoutes
{
    public const string CheckCloudIdentity = "/api/accounts/check-cloud-identity";
}
