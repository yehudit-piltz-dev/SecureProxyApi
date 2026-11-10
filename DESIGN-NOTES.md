# Design Notes

## Assumptions

### Request contract

The example request in section 4 of the brief is garbled. This solution reads it as:

```json
{ "clientId": "customer.get", "json": { "urlId": 42, "includeOrders": true } }
```

- `clientId` is the **operation key** (for example `customer.get`), not an identity of the caller.
- `json` is the parameters object. Per step 3 of the required processing sequence, it must contain `urlId`.
- For operations that require encryption, `json` is replaced by `encryptedJson`, which carries the same
  parameters object encrypted with AES-256-GCM.

No other envelope field is accepted, so a caller has no way to send a URL, HTTP method, path or header override.

## Scope decisions

### Caller authentication and per-caller authorization (not implemented)

The brief requires that only configured operations are forwarded, but does not ask the proxy to identify
its callers. In this solution, any caller that knows an operation key and supplies valid parameters can
execute that operation.

In a production deployment the proxy would also:

1. **Authenticate each calling system**, for example with OAuth 2.0 client credentials (a bearer token
   validated by the proxy) or mutual TLS.
2. **Authorize per operation**, with a `ClientOperationPermission` table that maps each calling system to
   the operation keys it may execute. A request for an operation the caller is not granted would be rejected
   before forwarding, like an unknown operation, and audited with the caller's identity.

It was left out to keep the assessment within its stated scope. The pipeline already has a single place
where it would go: right after the operation is resolved in `ProxyExecutor`, next to the
"operation disabled" check.
