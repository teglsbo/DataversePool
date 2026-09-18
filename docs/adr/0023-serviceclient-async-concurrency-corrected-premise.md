# ADR-0023: Corrected premise — a `ServiceClient` does not serialize concurrent async requests

## Status
Accepted

## Context
Since the first commit, this README's opening line justified pooling with "a `ServiceClient` only
handles one request at a time" — implying a single instance is a hard, client-side one-at-a-time
resource, similar to (say) a non-thread-safe `DbConnection`. That claim was never independently
verified against the SDK's actual behavior on the async path (`ExecuteAsync`/`RetrieveMultipleAsync`
etc.), which is what this library's own facade (`PooledOrganizationService`, ADR-0020) and most
real callers actually use.

Two independent checks show the claim is not accurate for the async path:

1. **SDK source.** Decompiling `Microsoft.PowerPlatform.Dataverse.Client.ServiceClient` shows
   `Command_Execute` (the synchronous path) wraps the underlying `DataverseService.Execute(req)`
   call in `lock (_lockObject)`. `Command_ExecuteAsyncImpl` (the path `ExecuteAsync` and all
   `IOrganizationServiceAsync2` methods actually use) has **no lock** around
   `DataverseServiceAsync.ExecuteAsync(req)` at all. The lock only exists on the path the async API
   surface doesn't use.
2. **Empirical measurement**, via a new opt-in integration test
   (`tests/ConnectionPool.Dataverse.Tests/LiveServiceClientConcurrencyTests.cs`,
   `Category=Integration`, not run in CI) against a real Dataverse environment: N sequential
   `WhoAmIRequest` calls through one `ServiceClient` instance were timed against N concurrent calls
   (`Task.WhenAll`) through the *same* instance. If the instance serialized requests client-side,
   concurrent time would track sequential time (ratio ≈ 1). Measured ratios instead dropped as N
   increased (N=20: ~0.2-0.5; N=50: ~0.1), with concurrent total time staying roughly flat regardless
   of N while sequential time scaled linearly with N — the signature of genuine concurrent execution,
   not a per-instance mutex. Numbers vary run-to-run and by environment/network conditions; the
   qualitative conclusion (not serialized) is the stable part, not any specific ratio.

This also surfaced a second, previously-undocumented issue while investigating: `CallerId` is a
plain instance property (`ServiceClient.CallerId` → `OrganizationWebProxyClient.CallerId`), read at
call time, not per-call or thread-local state. Two threads sharing one instance and setting different
`CallerId` values concurrently before executing can race — one thread's request could, in principle,
execute under the other's identity. This is a correctness risk, independent of whether requests
serialize or run concurrently, and was not something the original "one request at a time" framing
called out at all.

## Decision
- Correct the premise stated in the README and ADR-0020: drop the "one request at a time"/
  "single in-flight-request slot" framing as a literal client-side constraint.
- State the actual reasons to pool explicitly:
  1. Dataverse's **per-application-user service-protection concurrent-request ceiling** is a
     server-side limit, unaffected by how many concurrent calls a single client instance can issue.
     Round-robin across multiple application users (multiple pool members) is the only way to raise
     it — this was always the correct rationale and is unaffected by this correction.
  2. **Construction/clone cost** (ADR-0002) is real and orthogonal to per-instance concurrency
     behavior — still a valid reason to pool/reuse instead of constructing per request.
  3. **`CallerId`-style per-instance mutable state** makes sharing one instance across concurrent
     callers using *different identities* unsafe — pooling naturally avoids this by handing each
     lease exclusive use of an instance for its duration, but this is a correctness argument, not a
     throughput one.
  4. Health-aware routing/circuit-breaking around a misbehaving member (ADR-0007) remains valid and
     unrelated to this correction.
- Do **not** claim or rely on any specific concurrency ratio/number as a stable guarantee — the SDK's
  internal behavior here is undocumented and could change between versions; the integration test
  exists to let anyone re-verify this against their own environment/SDK version, not to pin a number.
- Leave `DataversePool`'s and `DataverseUserPool`'s existing exclusive-lease-per-checkout design
  unchanged. It was never *only* justified by the (incorrect) serialization claim, and remains correct
  for the reasons above (server-side quota, construction cost, and `CallerId` correctness).

## Consequences
- README and ADR-0020 updated to state the corrected reasons to pool instead of the inaccurate
  "one request at a time" framing.
- New opt-in integration test added so this can be independently re-verified against any real
  Dataverse instance and SDK version, without requiring credentials in CI.
- Open question, not resolved here: whether `DataverseUserPool` gains meaningful throughput from
  pooling *multiple clones of the same identity* versus reusing one instance carefully (given
  `CallerId` is fixed and not being raced), versus whether its value for a single application user is
  purely about avoiding reconstruction after a failure/health-check-fails scenario. Not changed by
  this ADR; left as a possible future investigation.
