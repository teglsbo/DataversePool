# ADR-0009: Return-scrubbing hook (CallerId leak) + documented single-process limitation

## Status
Accepted

## Context
A security review and a distributed-systems review were run against the entire codebase ahead of a
production-readiness assessment. Findings:

### 1. Security finding (fixed in this ADR): CallerId leak between leases (HIGH, 8/10)
`ResourcePool<T>.ReturnAsync` put a healthy resource back on the idle stack **unchanged**. For
Dataverse, that means: if a consumer sets `ServiceClient.CallerId` (Dataverse's built-in
"act as another user" impersonation field, a public settable `Guid` — confirmed via reflection) in
order to perform a call on behalf of an end user, and disposes its lease without resetting
`CallerId` itself, then **the same `ServiceClient` instance** — still impersonating the previous user
— would be handed out to the next, unrelated caller via `AcquireAsync`. This is exactly the kind of
cross-caller identity leak this pool (which explicitly must support multiple service users) has to
avoid.

This is a distinct and previously unaddressed risk relative to ADR-0003, which only discusses *concurrent*
sharing of one lease across threads during its lifetime — not *sequential* reuse after
`DisposeAsync`.

### 2. Distributed-systems finding (documented, NOT resolved in this ADR — see "Consequences")
A separate review focused on multi-instance/Kubernetes scenarios identified five
blocking issues, whose shared root cause is: **all pool, throttle, and circuit state exists only in memory,
per process**. With N instances sharing the same Dataverse service principals:

1. No shared budget across processes — N instances × MaxSize can far exceed the real
   per-app-user limit, and a 429 seen by instance A does not stop instance B from using the same budget.
2. "Fail open if all members are throttled/circuit-open" (deliberately chosen in ADR-0007/0008 to
   avoid deadlock) can amplify a real tenant-wide outage instead of providing backpressure.
3. The circuit breaker is not a true single-probe half-open — multiple concurrent callers can all
   hit the "recovering" member at the same time.
4. The circuit tracks only *creation failures* (clone failures), not operational failures — a tenant that
   degrades (slow calls, 5xx) without clone failing will not open the circuit.
5. No bounded acquire queue/deadline — under sustained overload, waiting callers can grow
   without limit.

## Decision

**Security finding #1 is fixed now:**
- `IPooledResourcePolicy<T>` got a new, optional (default no-op via a C# default interface method)
  method: `void OnReturned(T resource)`, called by `ResourcePool<T>.ReturnAsync` just before a healthy
  resource is placed back on the idle stack. This is domain-agnostic in Core (ADR-0001) — Core still
  does not know *what* is being scrubbed, only that the policy gets a chance to do it on return.
- `DataverseServiceClientPolicy.OnReturned` implements it by setting
  `resource.CallerId = Guid.Empty`.
- Tested: `ResourcePoolLifecycleTests.DisposeAsync_InvokesPolicyOnReturned_BeforeResourceIsReIdled`
  verifies the hook wiring itself (via `FakePolicy`); the `CallerId` reset itself is
  not unit-tested against a real `ServiceClient` (requires a live Dataverse connection — the same
  limitation as the rest of `DataverseServiceClientPolicy`), but it was verified via reflection that
  `CallerId` is a public settable `Guid` on the actual SDK type.

**Distributed-systems findings #1-5 are deliberately NOT resolved in this ADR.** Building real
multi-process coordination (shared budget, distributed half-open circuit, bounded backpressure across
instances) is a major architectural decision (would probably require an external coordinator —
Redis/Dataverse itself/another shared store — and changes the current Core property of having "no external
dependencies"). That should not be decided implicitly as an implementation detail.

Instead, it is documented explicitly as a **production limitation**:

> **DataversePool currently only supports correct operation when exactly one process instance owns a given
> set of Dataverse service principals at a time.** Do NOT run multiple instances/pods of the consuming
> application against the same `DataverseGroupPool` member set unless you explicitly accept that
> throttle/circuit state is not coordinated across them (i.e., effectively N× the intended
> service-protection budget, and no shared backpressure during a tenant-wide outage).

## Consequences
- **Security finding #1**: closed. The `OnReturned` hook is generic and can be reused for other
  future per-lease scrubbing needs without another breaking change.
- **Distributed-systems findings #1-5**: remain open, but are now explicitly documented (README +
  TODO.md) instead of being a silent gap. Prioritized backlog for a possible v2 "coordinated mode":
  1. External shared throttle/circuit state (budget bulkhead per tenant+app user).
  2. Replace unconditional fail-open with a configurable fail-fast/deadline policy.
  3. True single-probe half-open (atomic "probe-in-flight" per member).
  4. Distinguish creation failures from operational failures in the circuit signal.
  5. Bounded acquire queue/deadline (backpressure) in `ResourcePool<T>.AcquireAsync`.
- None of these require breaking the existing single-process API — they are additive.
