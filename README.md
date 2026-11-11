# Enterprise Secure Proxy Web API

A backend-only ASP.NET Core 8 Web API that forwards **only explicitly configured operations** to predefined
destination APIs. A caller names an operation by its key; the destination, HTTP method, path, allowed
parameters and headers all come from configuration stored in SQL Server, and a request that fails any check
never reaches the destination.

```http
POST /api/proxy/execute
Content-Type: application/json

{ "clientId": "customer.get", "json": { "urlId": 42, "includeOrders": true } }
```
is forwarded as `GET http://localhost:5240/customers/42?includeOrders=true`.

| Document | Content |
|---|---|
| [DESIGN-NOTES.md](DESIGN-NOTES.md) | Architecture, security controls, error handling, reliability, audit policy, trade-offs |
| [DEFECT-REPORT.md](DEFECT-REPORT.md) | Defects found and fixed during development |
| [AI-USAGE.md](AI-USAGE.md) | AI usage declaration |

## Solution layout

| Project | Purpose |
|---|---|
| `SecureProxy.Api` | The proxy and its admin API (EF Core migrations in `Data/Migrations`) |
| `SecureProxy.Tests` | xUnit integration and unit tests |
| `SecureProxy.MockUpstream` | Local stand-in for the sample destination ("customers API"), for manual runs |
| `SecureProxy.EncryptTool` | Prints an AES-256-GCM encrypted request envelope, for manual runs |

## Prerequisites

- **.NET 8 SDK**, any version from 8.0.100 (`global.json` selects the newest .NET 8 SDK installed).
- **SQL Server**: SQL Server Express **LocalDB** (installed with Visual Studio's "ASP.NET and web development"
  workload) is the default. Any SQL Server works; see [Using another SQL Server](#using-another-sql-server).
- Optional: **Visual Studio 2022** 17.9 or later.

## Setup

From the repository root:

```powershell
dotnet tool restore          # restores dotnet-ef 8.0.33 (local tool)
dotnet build
```

### Secrets

Secrets are never stored in the repository. Locally they come from user secrets of `SecureProxy.Api`:

```powershell
# 256-bit key for the sample encryption profile (KeyReference "customer-v1")
$key = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($key)
dotnet user-secrets set "EncryptionKeys:customer-v1" ([Convert]::ToBase64String($key)) --project SecureProxy.Api

# Admin API key (any long random value)
dotnet user-secrets set "Admin:ApiKey" ([guid]::NewGuid().ToString("N")) --project SecureProxy.Api
```

On Linux/macOS: `openssl rand -base64 32` for the key. The same values can be supplied as environment variables
(`EncryptionKeys__customer-v1`, `Admin__ApiKey`). Without them the proxy still runs: encrypted operations fail
closed (500 `encryption_unavailable`) and the admin API rejects every request (401).

### Database

In the `Development` environment the API applies the migrations and inserts sample configuration on startup,
so no manual step is needed. The default connection string (in `appsettings.json`) uses LocalDB and the
database `SecureProxy`.

To create or update the schema explicitly (for example in another environment):

```powershell
dotnet ef database update --project SecureProxy.Api
```

Sample configuration (`Data/DevelopmentDataSeeder.cs`): one destination `customers-api`
(`http://localhost:5240/`) and three operations:

| Key | Upstream call | Parameters |
|---|---|---|
| `customer.get` | `GET customers/{urlId}` | `urlId` integer (path, required), `includeOrders` boolean (query) |
| `customer.search` | `GET customers` | `urlId` string (query, required), `name` string (query, required), `page` integer (query) |
| `customer.update` | `PUT customers/{urlId}`, **encrypted** | `urlId` integer (path), `email` string (body, required), `phone` string (body) |

## Running

### Visual Studio
Open `SecureProxyApi.sln`, choose the launch profile **Proxy + Mock upstream** (defined in
`SecureProxyApi.slnLaunch`) and press F5. If the profile is not listed: right-click the solution →
*Configure Startup Projects* → *Multiple startup projects* → set `SecureProxy.Api` and
`SecureProxy.MockUpstream` to *Start*.

### Command line
```powershell
dotnet run --project SecureProxy.MockUpstream                      # http://localhost:5240
dotnet run --project SecureProxy.Api --launch-profile http         # http://localhost:5232
```

Swagger UI: <http://localhost:5232/swagger>. Use **Authorize** with the admin key for the admin endpoints.

### Trying it
`SecureProxy.Api/SecureProxy.Api.http` contains ready-made requests (valid, rejected, upstream errors,
timeout, admin API) for the Visual Studio / VS Code HTTP client. The mock upstream echoes what it received,
which shows exactly what the proxy forwarded. Special `urlId` values simulate upstream failures:
`404` (not found), `500` (server error with internal details, hidden by the proxy), `999` (slower than the
5-second timeout, so the proxy returns 504).

Encrypted operation:
```powershell
dotnet run --project SecureProxy.EncryptTool -- customer.update
```
prints an envelope with encrypted sample parameters (`@file.json` or a JSON argument replaces them). Send it
as the body of `POST /api/proxy/execute`; the mock upstream shows the decrypted body it received.

### Admin API
All admin endpoints require the header `X-Admin-Key`.

| Endpoint | Purpose |
|---|---|
| `GET/POST /api/admin/destinations`, `GET/PUT /api/admin/destinations/{id}` | Destinations |
| `GET/POST /api/admin/operations`, `GET/PUT /api/admin/operations/{id}` | Operations, including parameter and header rules |
| `GET /api/admin/configuration-changes?entityType=&entityKey=` | Configuration history |
| `GET /api/admin/request-logs?correlationId=&operationKey=` | Audit records |

`GET` returns an `ETag`; `PUT` requires it in `If-Match` (428 without it, 409 Conflict if the configuration changed
since it was read). Changes take effect on the next request (see the consistency model in DESIGN-NOTES.md).

## Tests

```powershell
dotnet test
```

The integration tests start the API in memory and create a temporary database from the migrations on
LocalDB (dropped at the end). No secrets or running services are needed: the tests supply their own keys and
replace every destination with a recording handler. Each rejection test checks that the destination
received nothing.

### Using another SQL Server
```powershell
$env:SECUREPROXY_TEST_SQL = "Server=localhost,1433;User Id=sa;Password=<password>;TrustServerCertificate=True"
dotnet test
```
For running the API against another server, set `ConnectionStrings__ProxyDatabase`.

### Coverage of the required scenarios (brief section 9)

| # | Scenario | Tests |
|---|---|---|
| 1 | Valid configured operation is forwarded | `ForwardingTests.Valid_operation_is_forwarded_to_the_configured_destination`, `Body_parameters_are_sent_as_json_with_only_validated_fields` |
| 2 | Unknown operation is rejected | `OperationPolicyTests.Unknown_operation_is_rejected`, `Operation_key_is_matched_exactly_including_case` |
| 3 | Disabled operation is rejected | `OperationPolicyTests.Disabled_operation_is_rejected`, `Disabling_an_operation_takes_effect_for_the_next_request` |
| 4 | Disabled destination is rejected | `OperationPolicyTests.Disabled_destination_is_rejected` |
| 5 | Caller-supplied destination URL cannot override configuration | `ForwardingTests.Envelope_fields_that_try_to_override_configuration_are_rejected`, `Url_sent_as_a_parameter_is_rejected_as_unknown`, `Url_inside_a_query_value_stays_a_value_and_cannot_change_the_destination` |
| 6 | Invalid or disallowed HTTP method is rejected | `ForwardingTests.Calling_the_proxy_with_a_method_other_than_post_is_rejected`, `Operation_configured_with_a_disallowed_method_is_never_forwarded`, `The_upstream_method_comes_from_configuration` |
| 7 | Missing required parameters are rejected | `ParameterTests.Missing_required_parameter_is_rejected`, `Missing_url_id_is_rejected`, `Null_required_parameter_counts_as_missing` |
| 8 | Unknown parameters follow the operation policy | `ParameterTests.Unknown_parameter_is_rejected_by_default`, `Unknown_parameter_is_accepted_but_not_forwarded_when_the_operation_allows_it` |
| 9 | Path traversal attempts are rejected | `ParameterTests.Path_traversal_attempts_are_rejected` (10 cases), `OperationPolicyTests.Operation_with_an_unsafe_path_template_is_never_forwarded` |
| 10 | Disallowed headers are not forwarded | `HeaderTests.Only_allowlisted_headers_are_forwarded`, `Sensitive_header_is_forwarded_only_when_explicitly_allowed`, `Upstream_response_headers_are_filtered` |
| 11 | Invalid AES-GCM authentication tags are rejected | `EncryptionTests.Invalid_authentication_tag_is_rejected` |
| 12 | Invalid encrypted payloads are never forwarded | `EncryptionTests` (tampered ciphertext, wrong key, replay against another operation, non-object plaintext, malformed envelopes, missing key) |
| 13 | Upstream timeout is handled | `UpstreamTests.Upstream_timeout_returns_504_and_cancels_the_upstream_call`, `Operation_timeout_overrides_the_destination_timeout` |
| 14 | Upstream HTTP error responses are handled | `UpstreamTests.Upstream_client_error_is_passed_through_and_marked_as_upstream`, `Upstream_server_error_becomes_502_without_leaking_the_upstream_body`, `Upstream_connection_failure_returns_502` |
| 15 | Cancellation is propagated | `UpstreamTests.Caller_cancellation_is_propagated_to_the_upstream_call` |
| 16 | Configuration concurrency conflicts are handled | `ConfigurationConcurrencyTests.Update_with_a_stale_version_returns_409_and_does_not_overwrite`, `Rules_only_change_also_requires_the_current_version`, `Rowversion_rejects_a_concurrent_write_at_the_database_level` |
| 17 | Audit records for successful and rejected requests | `AuditTests` (success, rejected, unknown operation, malformed request, no sensitive data stored) |

## Configuration reference

| Setting | Where | Default |
|---|---|---|
| `ConnectionStrings:ProxyDatabase` | `appsettings.json` / environment | LocalDB, database `SecureProxy` |
| `EncryptionKeys:<reference>` | user secrets / environment / secret manager | none |
| `Admin:ApiKey` | user secrets / environment | none (admin API disabled) |
| `Proxy:RateLimitPermits` / `RateLimitWindowSeconds` | `appsettings.json` | 100 per 10 s per client IP |
| `Proxy:AdminRateLimitPermits` / `AdminRateLimitWindowSeconds` | `appsettings.json` | 30 per 60 s per client IP |
| `Proxy:MaxConcurrentRequestsPerDestination` | `appsettings.json` | 50 |
| `ConfigurationCache.Seconds` | `OperationalSettings` table | 30 (0 disables caching) |
| `Proxy.MaxEnvelopeBytes` | `OperationalSettings` table | 65536 |
