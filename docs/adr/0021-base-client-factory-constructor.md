# ADR-0021: Base-client factory constructor for `DataverseServiceClientPolicy`/`DataverseUserPool`

## Status
Accepted

## Context
`DataverseServiceClientPolicy` (and `DataverseUserPool`, which wraps it) originally only accepted a
connection string, building the base client as `new ServiceClient(connectionString, logger)`. That
assumes authentication can be fully expressed as
`AuthType=ClientSecret;Url=...;ClientId=...;ClientSecret=...;`.

Not every enterprise consumer authenticates via a connection string, though - MSAL confidential-client
flows with a custom token-provider callback are a standard pattern for service principals using
certificate auth, managed identity token exchange, or a centralized token-caching service. That shape
looks like `new ServiceClient(instanceUri, tokenProviderFunction, useUniqueInstance: true, logger: ...)`,
which has no connection-string equivalent, so this policy needed a second way to obtain its base
client.

## Decision
Added a second constructor to both types accepting `Func<CancellationToken, Task<ServiceClient>>`
instead of a connection string:

```csharp
public DataverseServiceClientPolicy(Func<CancellationToken, Task<ServiceClient>> baseClientFactory, ILogger? logger = null, DataverseClientOptions? clientOptions = null)
public DataverseUserPool(string name, Func<CancellationToken, Task<ServiceClient>> baseClientFactory, PoolOptions? options = null, ILogger? logger = null, DataverseClientOptions? clientOptions = null)
```

Same guarantees as the connection-string path, unchanged:
- The factory is invoked at most once, serialized by the same base-init gate
  (`_baseInitGate`/docs/adr/0002) already used for the connection-string constructor - no risk of two
  concurrent callers double-authenticating.
- Every pooled slot is still produced by `ServiceClient.Clone(ILogger)` of the resulting base client;
  the factory itself is never invoked again to produce additional slots.
- `EnableAffinityCookie` forcing and `DataverseClientOptions` retry overrides (docs/adr/0016) apply
  identically to a factory-constructed base client and its clones.
- A base client the factory returns that isn't `IsReady` (or is `null`) is rejected the same way an
  unready connection-string-constructed base client is - `GetOrCreateBaseClientAsync` throws
  `InvalidOperationException` rather than caching a broken client.

Constructing the base client is still the caller's responsibility - this policy does not perform
token acquisition, retries, or caching of the factory's result beyond the single base client it
produces. That keeps `DataverseServiceClientPolicy` authentication-agnostic rather than growing
MSAL-specific (or any other provider-specific) code into the pooling library itself.

## Consequences
- Adopters whose authentication doesn't fit a connection string (MSAL, managed identity, custom
  token-provider callbacks, or anything else expressible as `Func<CancellationToken, Task<ServiceClient>>`)
  can now use `DataverseUserPool`/`DataversePool` without needing a connection-string shim.
- No behavior change for existing connection-string-based callers - this is a new, additive
  constructor overload on both types.
- Testing is scoped the same way as the rest of this policy: since `ServiceClient` cannot be
  constructed standalone in a unit test, coverage is wiring/failure/cancellation-focused (null-factory
  rejection, factory-exception propagation, a `null`-returning factory being rejected the same as a
  non-ready client, pre-canceled-token short-circuiting before the factory is ever invoked, and a
  failed factory attempt being retried - not cached - on the next acquire) rather than a genuine
  successful base-client construction.
