using System.Net;
using Google;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace GChannel.ApiService.Services;

/// <summary>
/// Translates Google Channel API failures into clean HTTP responses. A 429 (throttling) that
/// survives the client-side back-off is surfaced as 429 with a <c>Retry-After</c> hint so the UI
/// can ask the user to try again shortly, rather than returning a generic 500.
/// </summary>
public sealed class GoogleApiExceptionHandler(ILogger<GoogleApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            MissingGoogleTokenException =>
                (StatusCodes.Status401Unauthorized, "The Google access token is missing."),
            GoogleApiException { HttpStatusCode: var code } when (int)code != 0 =>
                ((int)code, MapTitle(code)),
            GoogleApiException =>
                (StatusCodes.Status502BadGateway, "The Google Channel API returned an error."),
            _ => (0, string.Empty)
        };

        if (status == 0)
        {
            return false; // Not a Google failure — let the default pipeline handle it.
        }

        if (status == StatusCodes.Status429TooManyRequests)
        {
            logger.LogWarning(exception, "Google Channel API throttled the request (429).");
            httpContext.Response.Headers.RetryAfter = "5";
        }
        else
        {
            logger.LogWarning(exception, "Google Channel API call failed with status {Status}.", status);
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = exception.Message
            },
            cancellationToken);

        return true;
    }

    private static string MapTitle(HttpStatusCode code) => code switch
    {
        HttpStatusCode.TooManyRequests => "The Google Channel API is throttling requests. Please retry shortly.",
        HttpStatusCode.Forbidden => "The Google account is not authorised for this operation.",
        HttpStatusCode.NotFound => "The requested Google resource was not found.",
        HttpStatusCode.Unauthorized => "The Google access token is invalid or expired.",
        _ => "The Google Channel API returned an error."
    };
}
