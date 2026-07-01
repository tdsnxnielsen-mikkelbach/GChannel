> Part of the [GChannel TODO index](../todo.md).

## Hardening

- [x] **Silent token refresh.** Google access tokens expire after ~1 hour. The refresh token is
  captured (`AccessType=offline`) and the Web app now silently refreshes the access token via
  `GoogleTokenProvider` before forwarding it to the API service, caching refreshed tokens in
  memory per user. The refresh happens in the **Web app** (not the API service as originally
  suggested) so the long-lived refresh token never leaves the front end — only short-lived access
  tokens are forwarded to the API, which remains a stateless Bearer consumer.
- [x] **Throttling / 429 handling.** Every Channel API call retries `429` (and transient `503`)
  with exponential back-off (`GoogleChannel:MaxRetryAttempts`, default 3). If retries are
  exhausted, `GoogleApiExceptionHandler` returns a clean `ProblemDetails` mirroring the upstream
  status (`429` with `Retry-After`, `403`, `404`, …) instead of a `500`; a missing token becomes
  `401`. See [architecture.md](architecture.md#resilience--throttling-http-429).
- [x] **Cloud Identity caching &amp; recheck.** Check results are cached in Redis and persisted to
  SQL (`IdentityCheckLogs`). The UI shows a **recently checked** list and a **recheck** action that
  bypasses the cache (`?refresh=true`) to re-query Google and refresh the cache.
- [x] **Request timeouts &amp; cancellation.** The shared resilience handler uses raised attempt/total
  timeouts (60s/120s) so cold-start Channel API calls aren't cut at the framework's default 30s, and
  benign client-aborted requests are classified as `499` (not `500`) by `GoogleApiExceptionHandler`.
- [x] **Local dev persistence.** SQL and Redis run as persistent-lifetime containers with named data
  volumes (`gchannel-sql-data`, `gchannel-redis-data`), so data survives between debug sessions and
  cold-start latency is avoided. See [deployment.md](deployment.md#run-locally).

