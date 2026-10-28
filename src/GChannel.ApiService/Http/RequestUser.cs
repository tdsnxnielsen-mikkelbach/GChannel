namespace GChannel.ApiService.Http;

/// <summary>Resolves the acting user for audit events: the authenticated identity, else the <c>X-User-Email</c> hint the Web app forwards.</summary>
public static class RequestUser
{
    public static string? NameOf(HttpContext? http)
    {
        if (http is null)
        {
            return null;
        }
        var name = http.User.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }
        var header = http.Request.Headers["X-User-Email"].ToString();
        return string.IsNullOrWhiteSpace(header) ? null : header;
    }
}
