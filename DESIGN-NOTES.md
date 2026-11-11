# Design Notes

The proxy is **configuration-driven, secure by default and fails closed**: a caller names an operation,
the proxy takes everything else (destination, method, path, allowed parameters and headers) from trusted
configuration in SQL Server, and nothing is sent upstream until every check has passed.

## Contents

1. [Assumptions](#1-assumptions)
2. [Architecture](#2-architecture)
3. [Request processing sequence](#3-request-processing-sequence)
4. [Data model](#4-data-model)
5. [Security controls](#5-security-controls)
6. [Error handling](#6-error-handling)
7. [Reliability and performance](#7-reliability-and-performance)
8. [Audit, logging and observability](#8-audit-logging-and-observability)
9. [Configuration history](#9-configuration-history)
10. [Testing approach](#10-testing-approach)
11. [Scope decisions and known limitations](#11-scope-decisions-and-known-limitations)

---

## 1. Assumptions

### Request contract

The example request in section 4 of the brief leaves the `json` value open (`"json": ???`). I read it as:

```json
{ "clientId": "customer.get", "json": { "urlId": 42, "includeOrders": true } }
```

- `clientId` is the **operation key** (for example `customer.get`), not an identity of the caller.
- `json` is the parameters object. Per step 3 of the required processing sequence, it must contain `urlId`.
  In the sample configuration `urlId` is the customer identifier, bound to the path (`customers/{urlId}`).
- For operations that require encryption, `json` is replaced by `encryptedJson`, which carries the same
  parameters object encrypted with AES-256-GCM:
  `{ "clientId": "customer.update", "encryptedJson": { "nonce": "…", "ciphertext": "…", "tag": "…" } }`.

No other envelope field is accepted, so a caller has no way to send a URL, HTTP method, path or header override.

### Defect report

No existing repository was supplied with the brief, so the solution was built from scratch.
`DEFECT-REPORT.md` therefore lists the defects I found and fixed while building it.

## 2. Architecture

A single ASP.NET Core 8 Web API (minimal APIs), one SQL Server database through EF Core 8, and `IHttpClientFactory` for
upstream calls.

| Folder (`SecureProxy.Api`) | Responsibility |
|---|---|
| `Domain/` | Entities: `Destination`, `Operation`, `ParameterRule`, `HeaderRule`, `EncryptionProfile`, `OperationalSetting`, `RequestLog`, `ConfigurationChange` |
| `Data/` | `ProxyDbContext` (timestamps and configuration history), entity configuration, migrations, development seed data |
| `Configuration/` | Loading operations as immutable, validated `OperationSnapshot`s, and caching them |
| `Proxy/` | The request pipeline: envelope parsing, parameter validation, upstream request building, forwarding, error catalogue |
| `Security/` | AES-256-GCM payload decryption and the `IKeySource` abstraction |
| `Audit/` | Request log writes, according to the audit policy |
| `Admin/` | Configuration API with optimistic concurrency |
| `Infrastructure/` | Correlation IDs, rate limiting, per-destination concurrency limit, options |

Other projects:

- `SecureProxy.Tests`: xUnit integration tests against the real API and a real SQL Server database, plus unit tests.
- `SecureProxy.MockUpstream`: a stand-in for the customers API, for manual runs only.
- `SecureProxy.EncryptTool`: prints an encrypted request envelope, for manual runs only.

I kept a single API project instead of separate domain, application and infrastructure assemblies. The
solution is small, and with folders the whole request flow can be followed in one place. The folders above
are where I would split it if the proxy grew.

## 3. Request processing sequence

`ProxyExecutor.ExecuteAsync` (`Proxy/ProxyExecutor.cs`) runs every request through these steps, in order.
Each check throws a `ProxyException` with a stable error code; nothing after the failing step runs.

| # | Step | Code |
|---|---|---|
| 1 | Only `POST` is accepted (other methods get an audited 405) | `ProxyExecutor` |
| 2 | Envelope: `Content-Type: application/json`, size limit enforced while reading, JSON depth ≤ 16, only `clientId` / `json` / `encryptedJson`, no duplicate fields (case-insensitive), `clientId` format, exactly one of `json` / `encryptedJson` | `ProxyEnvelope` |
| 3 | Operation lookup by exact key; operation enabled; destination present and enabled; configuration valid; request within the operation's `MaxRequestBytes` | `OperationConfigurationProvider`, `OperationSnapshotFactory` |
| 4 | Decryption when the operation requires it (and rejection of encrypted payloads when it does not) | `PayloadDecryptor` |
| 5 | Parameters: `urlId` present, no unknown parameters (unless allowed), required present, type, length, range, pattern, path-segment safety | `ParameterValidator` |
| 6 | Upstream request built only from configuration and validated values; header allowlist applied | `UpstreamRequestFactory` |
| 7 | Per-destination concurrency permit | `DestinationConcurrencyLimiter` |
| 8 | Audit record written (**mandatory**: if it fails, the request is not forwarded) | `RequestAuditor` |
| 9 | Forward with the configured timeout and the caller's cancellation; stream the response back with a size limit | `UpstreamForwarder` |
| 10 | Final audit outcome and a structured log line, in a `finally` block, for every outcome | `ProxyExecutor` |

## 4. Data model

| Table | Purpose | Notes |
|---|---|---|
| `Destinations` | Upstream APIs | Unique `Name`; `BaseUrl`; `IsEnabled`; `TimeoutMs`; timestamps; `rowversion` |
| `Operations` | Allowed actions | Unique `Key`; FK to destination; `HttpMethod`; `PathTemplate`; `IsEnabled`; `MaxRequestBytes`; `MaxResponseBytes`; optional `TimeoutMs` override; `RequiresEncryption` + `EncryptionProfileId`; `AllowUnknownParameters`; timestamps; `rowversion` |
| `ParameterRules` | Per-operation parameter validation | Unique per operation and name; `Location` (Path / Query / Body); `IsRequired`; `DataType`; `MaxLength`; `MinValue` / `MaxValue`; `Pattern` |
| `HeaderRules` | Per-operation header allowlist | `AllowSensitive` must be set to forward `Authorization`, `Cookie`, `X-Api-Key`, `X-Auth-Token` |
| `EncryptionProfiles` | Key metadata | `Algorithm` (`AES-256-GCM`) and `KeyReference` only; **no key material** |
| `OperationalSettings` | Runtime-tunable settings | `ConfigurationCache.Seconds` (default 30), `Proxy.MaxEnvelopeBytes` (default 64 KB) |
| `RequestLogs` | Audit of every execution attempt | No foreign keys, so the audit survives configuration changes; no payloads or headers |
| `ConfigurationChanges` | Configuration history | Written in the same transaction as each change |

Check constraints guard the basic invariants (positive sizes and timeouts, a profile when encryption is
required). Deleting a destination or profile still used by an operation is restricted.

## 5. Security controls

### Trusted configuration only
- The destination URL, HTTP method and path come from the `Operations` and `Destinations` rows. The envelope
  has no field for them, and unknown envelope fields are rejected.
- Configuration is validated **every time it is loaded**, not only when saved through the admin API, so a row
  edited directly in the database cannot make the proxy forward something unsafe. Invalid configuration
  (a method other than GET/POST/PUT/PATCH/DELETE, a base URL with credentials, query or a non-http(s)
  scheme, an absolute or `..` path template, placeholders without matching path rules, body parameters on
  GET or DELETE, encryption without a profile, an invalid regular expression) is never forwarded (500 `operation_misconfigured`,
  details logged internally only).
- Operation keys are compared exactly (ordinal). SQL Server's default collation is case-insensitive, so
  `Customer.Get` would otherwise resolve to `customer.get`.

### Path and query construction
- Path parameters must be whole path segments; values are rejected if empty, `.`, `..`, containing `..`, or
  any of `/ \ % ? #`, and are then escaped with `Uri.EscapeDataString`.
- Query values are always escaped, so `&`, `=`, `#` in a value cannot add parameters.
- After building, the URI is checked again: same scheme, host and port as the base URL, and a path under
  the base path. This is defence in depth: it should never fail if the checks above are correct.

### Parameters
- Strict JSON types: an `Integer` rule accepts a JSON number only, not `"42"`. Only flat scalar values are accepted.
- Regular expressions are anchored and run with a 100 ms timeout (no catastrophic backtracking).
- Duplicate parameter names, in any casing, are rejected (parameter pollution).
- Unknown parameters are rejected by default. When an operation sets `AllowUnknownParameters`, they are
  accepted but **dropped**: only validated values are ever forwarded.

### Headers
- Request headers are forwarded only when listed in the operation's `HeaderRules`.
- Never forwarded, even when listed: hop-by-hop headers (`Connection`, `Keep-Alive`, `TE`, `Trailer`,
  `Transfer-Encoding`, `Upgrade`, `Proxy-*`), headers named in `Connection`, headers the proxy sets itself
  (`Host`, `Content-Length`, `Content-Type`, `X-Correlation-ID`), and routing/identity headers a caller
  could spoof (`Forwarded`, `X-Forwarded-*`, `X-Real-IP`).
- Sensitive headers (`Authorization`, `Cookie`, `X-Api-Key`, `X-Auth-Token`) also need `AllowSensitive`.
- Response headers are allowlisted too (`Content-Type`, `Content-Language`, `Cache-Control`, `ETag`,
  `Last-Modified`, `Expires`, `Retry-After`). `Location` and `Set-Cookie` never reach the caller, so internal
  hostnames and upstream sessions do not leak.

### Encryption (AES-256-GCM)
- The database stores only the profile's `KeyReference`. Keys come from `IKeySource`; the implementation
  used here, `ConfigurationKeySource`, reads `EncryptionKeys:<reference>` from configuration, which locally
  means user secrets or environment variables (never `appsettings.json`). In production, `IKeySource` would
  be backed by a secret manager such as Azure Key Vault, with the same interface.
- The GCM tag authenticates the ciphertext; a tampered tag, ciphertext or nonce, a wrong key, a non-JSON or
  invalid plaintext are all rejected (400 `invalid_encrypted_payload` or parameter errors) and never forwarded.
- The operation key is the **associated data**, so a payload encrypted for one operation cannot be replayed
  against another.
- Key bytes and decrypted plaintext are zeroed after use. Keys, plaintext and payloads are never logged.
- The proxy decrypts, validates and forwards the parameters over the upstream connection (HTTPS in
  production). Re-encrypting for the destination or encrypting responses is out of scope.

### Transport and other measures
- Redirects are not followed (`AllowAutoRedirect = false`): a redirect could lead outside the destination.
- Cookies are not stored, and automatic decompression is off (no decompression bombs).
- The default `HttpClient` logging is removed, because it logs full URLs including query-string values.
- Errors never contain stack traces, hostnames, keys, configuration details or upstream 5xx bodies.
- No `UseHttpsRedirection`: redirecting an API call does not protect a body already sent in clear text.
  HTTPS is enforced at the hosting layer (HTTPS-only Kestrel endpoint or the ingress/load balancer, with HSTS).
- The W3C `traceparent` header is added to upstream calls by .NET for distributed tracing. It carries no
  caller data; it can be turned off with `DistributedContextPropagator` if destinations must not receive it.
- The admin API requires `X-Admin-Key`, compared in constant time, and has a stricter rate limit than the proxy.

## 6. Error handling

Every proxy-generated error is an RFC 9457 problem response with a stable `code`, a safe `title` and the
`correlationId`. The response header `X-Proxy-Response-Source` is `proxy` for these and `upstream` for responses
that came from the destination.

| Code | Status | When |
|---|---|---|
| `method_not_allowed` | 405 | Anything other than POST |
| `invalid_content_type` | 415 | Not `application/json` (UTF-8) |
| `request_too_large` | 413 | Over the global envelope limit or the operation's `MaxRequestBytes` |
| `invalid_envelope` / `unknown_envelope_field` / `invalid_client_id` | 400 | Malformed or unexpected envelope |
| `unknown_operation` | 404 | No operation with that exact key |
| `operation_disabled` | 403 | Operation disabled |
| `destination_unavailable` | 403 | Destination missing or disabled |
| `operation_misconfigured` | 500 | Stored configuration failed validation |
| `encryption_required` / `encryption_not_allowed` / `invalid_encrypted_payload` | 400 | Encryption mismatch or invalid payload |
| `encryption_unavailable` | 500 | Profile disabled or key missing |
| `invalid_parameters` / `missing_url_id` / `missing_parameter` / `unknown_parameter` / `invalid_parameter` / `invalid_path_parameter` | 400 | Parameter validation (`parameter` names the configured parameter, never caller input) |
| `rate_limited` | 429 | Rate limit exceeded, with `Retry-After` |
| `destination_busy` | 503 | Per-destination concurrency limit reached |
| `configuration_unavailable` / `audit_unavailable` | 503 | SQL Server unavailable (fail closed) |
| `upstream_timeout` | 504 | Destination exceeded the configured timeout |
| `upstream_unavailable` | 502 | Connection failure |
| `upstream_error` | 502 | Destination returned 5xx; `upstreamStatus` carries the original status |
| `upstream_response_too_large` | 502 | Response over `MaxResponseBytes` |
| `internal_error` | 500 | Unexpected failure |

Upstream status handling:
- **1xx–4xx** are passed through with their (size-limited) body and allowlisted headers. A 404 or 409 from the
  destination is meaningful to the caller.
- **5xx** are replaced by 502 `upstream_error` with `upstreamStatus`. A 5xx body often contains stack traces or
  internal names, and from the caller's point of view the gateway's upstream failed.

## 7. Reliability and performance

### Timeouts and cancellation
- The timeout is the operation's `TimeoutMs`, or else the destination's `TimeoutMs`. `HttpClient.Timeout` is
  infinite; the proxy creates a `CancellationTokenSource` with the configured timeout and links it with
  `HttpContext.RequestAborted`.
- The linked token covers sending, waiting for headers and streaming the body.
- Caller cancellation and upstream timeout are told apart by checking which token fired. An upstream timeout
  returns 504 `upstream_timeout`. Caller cancellation cancels the upstream call and is audited as
  `CallerCancelled` (status 499 in the audit only, since nobody is listening).

### HTTP client lifecycle
One named client (`upstream`) from `IHttpClientFactory` with a `SocketsHttpHandler`: pooled connections,
`PooledConnectionLifetime` of 2 minutes so DNS changes are picked up, 5-second connect timeout. No client is
created or disposed per request.

### Streaming and size limits
- **Request**: the envelope is read with a hard limit (default 64 KB, `Proxy.MaxEnvelopeBytes`) that stops reading
  as soon as it is exceeded, and Kestrel's body limit is lowered to match. The envelope is buffered on purpose:
  it must be fully validated (and possibly decrypted) before anything is forwarded, so it cannot be streamed
  through. The bound keeps the buffer small. The operation's `MaxRequestBytes` is then checked.
- **Response**: requested with `HttpCompletionOption.ResponseHeadersRead` and streamed to the caller in 16 KB
  chunks. A `Content-Length` over `MaxResponseBytes` is refused before anything is sent (502). Without
  `Content-Length`, the limit is enforced while streaming; since the status line has already been sent, the
  connection is aborted so the caller sees a truncated response rather than a seemingly complete one.

### Retries and idempotency
I chose not to add retries: **the proxy never retries**. Once a request may have reached the destination,
sending it again can process it twice, and the proxy cannot tell whether that is safe for a given destination.

Which failures could be retried safely, if retries were added:
- Failures where the request certainly did not reach the destination (DNS failure, connection refused),
  for any method.
- Timeouts, 502/503/504 and connection resets for **idempotent** operations only (GET, HEAD, PUT, DELETE by HTTP
  semantics), and only if the destination honours those semantics. This would be an explicit `IsIdempotent`
  flag on the operation rather than an inference from the method.
- Never for POST or PATCH, unless deduplication is in place.

Preventing duplicate processing for POST: the destination must support an idempotency key or deduplication.
The proxy would then send a stable key per logical request, either the caller's `Idempotency-Key` (allowlisted
as a header and validated) or one derived from the correlation ID, so that a retry by the proxy or by the
caller is recognised by the destination. The proxy itself could also store recent keys with their responses
and replay them, at the cost of a store shared by all instances.

### Configuration caching and the consistency model
- Snapshots are cached per process in `IMemoryCache` for `ConfigurationCache.Seconds` (default 30, 0 disables
  caching). Operational settings are cached for 60 seconds.
- Every successful admin write evicts the whole cache of that instance (`ConfigurationCache.InvalidateAll`).
  The eviction token is captured before a load starts, so a change made during a load also evicts the value
  being loaded.
- Unknown keys are not cached, so random keys cannot fill memory.
- **Consistency model**: a change made through the admin API applies to the next request on the instance that
  made it; other instances apply it within the cache lifetime (at most 30 seconds by default). A change made
  directly in the database applies everywhere within the cache lifetime. For a multi-instance deployment that
  needs immediate propagation, the next step is a change notification (SQL Server change tracking polling or a
  pub/sub message) that calls `InvalidateAll` on every instance.

### Concurrency of configuration updates
- `Destinations` and `Operations` have a SQL Server `rowversion`. The admin API returns it as an `ETag`; an
  update must send it in `If-Match`. EF Core issues `UPDATE … WHERE RowVersion = @original`; if another update
  happened in between, no row matches and the API returns **409 Conflict** without overwriting. A missing
  `If-Match` returns 428.
- A PUT of an operation always touches the operation row, so a change to its parameter or header rules
  (separate tables) also goes through the rowversion check and produces a new version.

### Requests already executing when an operation or destination is disabled
A request works with the snapshot it loaded at the start. Requests already past the lookup complete normally;
requests that start after the change (and, on other instances, after the cache expires) are rejected. This avoids
cutting off an upstream call halfway, which could leave the destination in an unknown state. An immediate
kill switch would need a registry of in-flight requests per operation, so they can be cancelled; I did not
build one.

### Resilience
- **Rate limiting** (implemented): fixed window per client IP, 100 requests per 10 seconds for the proxy and
  30 per minute for the admin API (configurable in `Proxy`). Rejections return 429 with `Retry-After`. Behind a
  load balancer, forwarded headers from trusted proxies must be configured so the limit applies to real clients.
- **Bounded concurrency** (implemented): at most 50 concurrent requests per destination (configurable). Excess
  requests fail fast with 503 `destination_busy` instead of queuing, so one slow destination cannot exhaust
  threads and connections for the others.
- **Circuit breaking** (discussed): a per-destination circuit breaker (for example
  `Microsoft.Extensions.Http.Resilience` / Polly) that opens after repeated 5xx, timeouts or connection failures
  and rejects calls for a short break, would protect a struggling destination and fail callers fast. I left
  it out because a breaker has to be scoped and tuned per destination (one named client per destination, or a
  keyed pipeline), which is more than the sample configuration needs. Any resilience pipeline added later must
  keep retries off, or limit them to idempotent operations as described above.

## 8. Audit, logging and observability

### Correlation
`CorrelationIdMiddleware` accepts a well-formed `X-Correlation-ID` (`[A-Za-z0-9._-]{8,64}`) or generates one. It
becomes `HttpContext.TraceIdentifier`, is returned in the response, sent upstream, written to the audit
record and added to the logging scope of every log entry of the request.

### Audit policy
- **Every execution attempt that reaches the proxy endpoint** gets one `RequestLogs` record: successful,
  rejected (including malformed envelopes and wrong methods) and failed.
- **Before forwarding**, the record is inserted with outcome `InProgress`. This write is **mandatory**: if it
  fails, the request is not forwarded (503 `audit_unavailable`). Every request that reached a destination
  therefore has an audit record.
- The **final outcome** is written best-effort after the response. Rejected requests (never forwarded) are
  also recorded best-effort.
- **Rate-limited requests are not audited**, only logged: writing a row per request during a flood would turn
  the rate limiter into a way to overload the database.
- Recorded: correlation ID, operation key, operation and destination IDs, start and completion time, duration,
  whether it was forwarded, upstream status, proxy status, outcome category and reason code.
- **Never recorded** (neither in the audit nor in logs): parameter values, payloads, headers, cookies,
  credentials, encryption keys.

### When SQL Server is unavailable
- Configuration cannot be loaded (unless cached): requests fail closed with 503 `configuration_unavailable`.
- The pre-forwarding audit write fails: the request is **not** forwarded (503 `audit_unavailable`).
- A best-effort write fails (final outcome, rejected request): the full record is written to the structured
  log instead, so log aggregation still has it.

The consequence is that the proxy is only as available as SQL Server. I chose this on purpose: for a
security gateway, "every forwarded request is audited" matters more than staying up during a database outage.
The alternative is to buffer audit records (in memory or a local outbox) and forward anyway. The proxy then
keeps working while the database is down, but records are lost if the process dies before they are written.
That trade-off could be made per operation, for operations where availability matters more than the audit trail.

### Logging
Structured logging through `ILogger` with message templates. One summary line per request (outcome,
operation, reason, statuses, forwarded, duration), warnings for timeouts and upstream failures, errors for
misconfiguration and audit failures.

## 9. Configuration history

`ProxyDbContext.SaveChanges` writes a `ConfigurationChanges` row for every added, modified or deleted
configuration entity, in the same transaction as the change: entity type and key, change type, time, author
(the authenticated admin, `system` for seed data), and a JSON object with the old and new values of the changed
properties. Rows for new entities are written after their keys are generated. Concurrency tokens are excluded
(`[NotInHistory]`), and no secrets exist in configuration to leak (keys are references only). The admin API
exposes the history at `GET /api/admin/configuration-changes`.

## 10. Testing approach

- Integration tests run the real application in memory (`WebApplicationFactory`) against a real SQL Server
  database created from the migrations and dropped afterwards. Real SQL Server matters here: `rowversion`
  concurrency and the case-insensitive collation behave differently on in-memory providers.
- Every destination is replaced by a recording handler. **Every rejection test asserts that the destination
  received nothing**, as the brief requires, not only the status code.
- Unit tests cover configuration validation and the cache's invalidation behaviour.
- `README.md` maps each scenario of section 9 to its tests.

## 11. Scope decisions and known limitations

### Caller authentication and per-caller authorization (not implemented)

The brief requires that only configured operations are forwarded, but does not ask the proxy to identify its
callers. As built, any caller that knows an operation key and supplies valid parameters can execute that
operation.

In a production deployment the proxy would also:

1. **Authenticate each calling system**, for example with OAuth 2.0 client credentials (a bearer token
   validated by the proxy) or mutual TLS.
2. **Authorize per operation**, with a `ClientOperationPermission` table that maps each calling system to the
   operation keys it may execute. A request for an operation the caller is not granted would be rejected
   before forwarding, like an unknown operation, and audited with the caller's identity.

I left it out to stay within the scope of the brief. There is one clear place for it in the pipeline: right
after the operation is resolved in `ProxyExecutor`, next to the "operation disabled" check.

### Other limitations
- **Admin API scope**: destinations and operations (with their rules) can be created, read and updated.
  Encryption profiles and operational settings are managed directly in the database; delete endpoints are not
  provided (disable instead, which keeps the history meaningful).
- **Admin authentication** is a single shared key. Production would use the identity provider with per-user
  roles, which would also make `ChangedBy` a real user.
- **Key rotation**: a new key is a new `KeyReference` (for example `customer-v2`) on the profile. Accepting both
  keys during a transition would need the envelope to name the key version.
- **Multi-instance cache invalidation** is TTL-based (see the consistency model).
- **Destination authentication** (the proxy presenting its own credentials to a destination) is not
  implemented; it would be a reference on the destination to a secret in `IKeySource`, added by the proxy and
  never taken from the caller.
