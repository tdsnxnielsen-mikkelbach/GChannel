using GChannel.ApiService.Configuration;
using GChannel.Shared.Contracts;
using Google.Apis.Cloudchannel.v1;
using Google.Apis.Cloudchannel.v1.Data;
using Google.Apis.Http;
using Google.Apis.Services;
using Google.Apis.Util;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;

namespace GChannel.ApiService.Services;

// HTTP retry / pacing infrastructure — see IGoogleChannelClient for the contract documentation.
public sealed partial class GoogleChannelClient
{
    /// <summary>
    /// Unsuccessful-response handler that retries 429/503 responses, honouring the server's
    /// <c>Retry-After</c> header (the Channel API sends it on quota errors) and otherwise falling back
    /// to exponential back-off with jitter. Waits are capped and cancellable.
    /// </summary>
    private sealed class RetryAfterBackOffHandler(int maxRetries, TimeSpan maxDelay) : IHttpUnsuccessfulResponseHandler
    {
        public async Task<bool> HandleResponseAsync(HandleUnsuccessfulResponseArgs args)
        {
            if (!args.SupportsRetry
                || args.CurrentFailedTry > maxRetries
                || args.Response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable))
            {
                return false;
            }

            var delay = RetryAfterDelay(args.Response) ?? ExponentialBackOff(args.CurrentFailedTry);
            if (delay > maxDelay)
            {
                delay = maxDelay;
            }

            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, args.CancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            return true;
        }

        private static TimeSpan? RetryAfterDelay(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter is null)
            {
                return null;
            }

            if (retryAfter.Delta is { } delta)
            {
                return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
            }

            if (retryAfter.Date is { } date)
            {
                var until = date - DateTimeOffset.UtcNow;
                return until > TimeSpan.Zero ? until : TimeSpan.Zero;
            }

            return null;
        }

        private static TimeSpan ExponentialBackOff(int attempt)
        {
            // 1s, 2s, 4s, ... plus up to 1s of jitter to de-correlate concurrent retries.
            var seconds = Math.Pow(2, Math.Max(0, attempt - 1));
            return TimeSpan.FromSeconds(seconds + Random.Shared.NextDouble());
        }
    }

    /// <summary>
    /// Paces calls to at most one per <c>interval</c> (a token-bucket of size 1) so a burst of
    /// concurrent dashboard <c>entitlements.list</c> calls stays under the Channel API's per-minute
    /// quota instead of triggering 429s. Thread-safe; <c>WaitAsync</c> returns when the caller's slot
    /// is due (or throws if cancelled first).
    /// </summary>
    private sealed class RequestPacer(TimeSpan interval)
    {
        private readonly Lock _gate = new();
        private DateTimeOffset _nextSlot = DateTimeOffset.MinValue;

        public Task WaitAsync(CancellationToken cancellationToken)
        {
            DateTimeOffset slot;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                slot = _nextSlot > now ? _nextSlot : now;
                _nextSlot = slot + interval;
            }

            var delay = slot - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;
        }
    }
}
