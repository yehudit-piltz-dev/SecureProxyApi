# Defect Report

## Context

No existing codebase was supplied with the brief, so the solution was built from scratch, starting from the
standard Visual Studio Web API template.

So this report lists the defects I **found and fixed while building the solution**: in the template and in
earlier versions of my own code. They came up while running the application, checking its output, and in
code review. Each one is fixed at its root cause and, where it can be tested, covered by a test.

## Defects

| # | File / component | Observed behavior | Root cause | Risk or impact | Fix | Test |
|---|---|---|---|---|---|---|
| D1 | `Program.cs`: proxy endpoint mapping | A request with `Content-Type: text/plain` got a bare `415` with an empty body, no `X-Proxy-Response-Source` header and **no audit record**. | The endpoint declared `.Accepts<ProxyEnvelopeDocument>("application/json")` to document the body in Swagger. Routing uses that metadata to reject other content types **before** the handler runs, so the proxy's own validation, error format and audit were bypassed. | Rejected requests missing from the audit trail; inconsistent error contract for callers. | Removed `Accepts`; the request body is documented in Swagger by `ProxyEnvelopeOperationFilter` instead, and the content type is validated by `ProxyEnvelope`. | `EnvelopeTests.Content_type_other_than_json_is_rejected` (checks the problem body and source header), `AuditTests.Malformed_request_is_audited_too` |
| D2 | `Data/ProxyDbContext.cs`: configuration history | Found in code review of the first version, before it ran: history rows of newly created entities would have recorded wrong keys (the entity's own key and foreign keys such as a parameter rule's `OperationId`). | History was captured in `SaveChanges` **before** saving. For added entities EF Core holds temporary key and foreign key values until the insert assigns the real identity values. | Configuration history pointing to non-existent entities, so changes could not be traced. | Added entities are described **after** the first save, inside the same transaction, when real keys are known; modified and deleted entities are still described before saving (their original values are needed). | `ConfigurationConcurrencyTests.History_of_new_entities_records_their_real_keys_and_enum_names` |
| D3 | `Data/ProxyDbContext.cs`: configuration history | Enum values in the history were numbers, for example `"Location":{"new":0}`. | `JsonSerializer` writes enums as integers by default. | History hard to read during an investigation, and silently wrong if enum members are ever reordered. | History JSON uses `JsonStringEnumConverter` (`"Location":{"new":"Path"}`). | Same test as D2 |
| D4 | `Proxy/ProblemWriter.cs`: error responses | Code review of the first version: proxy errors were written without a `Content-Type`, and the write task's failure was swallowed by a `ContinueWith`. | Missing `Content-Type: application/problem+json`; unnecessary continuation instead of `await`. | Callers could not recognise error responses as problem details; write failures hidden. | Set `application/problem+json` and await the write. | `ProxyTestBase.AssertProxyError` checks the media type in every rejection test |
| D5 | `Program.cs` (template): `UseHttpsRedirection` | Running on HTTP only logged "Failed to determine the https port for redirect"; with the template's `https` launch profile the middleware redirects every HTTP call to HTTPS. | Template default meant for browser applications. | For an API, the request body (possibly sensitive parameters) has already been sent in clear text before the redirect, and clients that follow the redirect hide the misconfiguration. A false sense of security. | Removed; HTTPS is to be enforced by the hosting configuration (HTTPS-only endpoint or ingress). Documented in DESIGN-NOTES.md, section 5. | Not unit-testable; see DESIGN-NOTES.md |
| D6 | `global.json` (template) | Found in the final review: the solution could only be built with .NET SDK 8.0.427 or newer. | The template pinned `"version": "8.0.427"` with `rollForward: latestFeature`, which sets 8.0.427 as the **minimum**. | On a reviewer's machine with an earlier .NET 8 SDK (for example 8.0.300), every `dotnet` command fails with "A compatible .NET SDK was not found", so the solution is not reproducible. | `"version": "8.0.100"`: any .NET 8 SDK is accepted, and the newest installed 8.0 SDK is used. README updated. | Build verified with the relaxed pin |

## Defects in the tests themselves

| # | Test | Observed | Root cause | Fix |
|---|---|---|---|---|
| T1 | `ForwardingTests.Envelope_fields_that_try_to_override_configuration_are_rejected` | Failed for the case `ClientId` (capital C): expected `unknown_envelope_field`, got `invalid_envelope`. | The test's expectation was wrong, not the proxy. `ClientId` next to `clientId` is a duplicate under the case-insensitive duplicate check, which runs first; the request was correctly rejected and not forwarded. | Moved the case to `EnvelopeTests.Duplicate_envelope_field_is_rejected`, with the correct expected code. |
