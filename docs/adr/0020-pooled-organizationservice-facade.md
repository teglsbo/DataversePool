# ADR-0020: `PooledOrganizationService` - an `IOrganizationServiceAsync2` facade over the pool

## Status
Accepted

## Context
External feedback from a real prospective adopter (a high-concurrency Dataverse ingestion pipeline
doing a large volume of sequential `RetrieveMultiple` existence-check round-trips per run, independently
investigating why raising its own app-level concurrency didn't scale reads) identified a concrete
integration gap: most existing codebases inject a long-lived, constructor-injected
`IOrganizationServiceAsync`/`IOrganizationServiceAsync2` - the standard way to consume this SDK.
Adopting `DataversePool` as written required restructuring every such call site to an explicit
"acquire a `DataverseLease`, use `lease.Resource`, dispose the lease" pattern per call. For a
codebase with many existing call sites built against the standard interface, that rewrite is a real
adoption blocker, not just an inconvenience.

The same feedback also confirmed (via `Utils.IsRequestValidForTranslationToWebAPI` in the vendored
SDK) that reads (`RetrieveMultiple`, etc.) never route through the Web API/HTTP path regardless of
the `UseWebApi` setting - only `Create`/`Update`/`Delete`/`ImportSolution`/`ExportSolution`/
`StageSolution` are eligible. So for a read-heavy workload, pooling genuinely is the only available
lever for concurrency; there's no way to work around the single-in-flight-request-per-`ServiceClient`
constraint via configuration.

## Decision
- Added `PooledOrganizationService`, implementing `IOrganizationServiceAsync2` (which itself extends
  `IOrganizationServiceAsync` and `IOrganizationService`, so this facade covers all three - every sync
  and async member the SDK's own consumers might already depend on).
- Two constructors: one wrapping a `DataversePool` (multi-member, round-robin/health-aware), one
  wrapping a single `DataverseUserPool` directly (so a caller with exactly one application user isn't
  forced to construct a one-member `DataversePool` first just to get this facade).
- Every interface member acquires exactly one `DataverseLease`, invokes the corresponding method on
  the leased `ServiceClient`, and releases the lease before returning - including when the call
  throws or the acquire itself is canceled. This acquire/run/release pattern is factored into a
  small, generic, Dataverse-independent internal helper (`LeaseScope`) rather than repeated inline
  per method, both to avoid duplicating the exception-safety logic ~18 times and, more importantly,
  so that logic is directly unit-testable against a fake lease type (see Consequences).
- The synchronous `IOrganizationService` members required by the interface (`Create`, `Retrieve`,
  etc.) block on their `*Async` equivalent via `GetAwaiter().GetResult()`. A pool is fundamentally an
  async-acquire abstraction (`ResourcePool<T>.AcquireAsync`) - there is no synchronous acquire path to
  implement these against instead, so blocking is the only option for satisfying the interface's
  required synchronous surface. Callers should prefer the `*Async` members directly wherever they
  control the call site.
- Deliberately **not** included: retry-on-throttle behavior, or reporting throttle signals back to
  the pool. `DataversePool.ExecuteWithThrottleRetryAsync` (ADR-0017/0019) already covers that, but it
  needs a `DataverseLease` in hand to report against - exposing that would mean this facade's surface
  is no longer identical to the plain SDK interface, defeating the point of a drop-in adapter. A
  caller that wants throttle-aware retry and is willing to give up interface-parity should use
  `ExecuteWithThrottleRetryAsync` directly instead of this facade.

## Consequences
- Adopting `DataversePool` for an existing codebase built around constructor-injected
  `IOrganizationServiceAsync`/`IOrganizationServiceAsync2` can now be a pure DI-registration change
  (register `PooledOrganizationService` where the interface is requested) instead of a rewrite of
  every call site.
- No behavior change for anything already using `DataverseLease`/`DataversePool`/`DataverseUserPool`
  directly - this is a new, additive, opt-in type.
- **Testing, honestly scoped**: `ServiceClient` cannot be constructed standalone (private
  parameterless constructor, requires a live connection - see `DataverseClientOptionsTests`'s
  remarks), so the actual per-method SDK delegation (does `RetrieveAsync` really call
  `ServiceClient.RetrieveAsync` with the right arguments) is not independently unit-testable, and
  each method is deliberately a trivial one-line delegation to minimize that risk. What *is*
  thoroughly unit-tested:
  - `LeaseScope` (the shared acquire/run/release primitive) against a fake lease type: the lease is
    released exactly once on success, on the operation throwing, and on the operation being
    canceled; the original exception instance (not just type) propagates unchanged; a failed acquire
    is not swallowed or retried; the `CancellationToken` passed to the facade is the same instance
    observed by `acquire`.
  - `PooledOrganizationService`'s wiring: both constructors reject `null`; calling any interface
    member against a pool backed by an invalid ("dummy") connection string throws (proving the
    facade genuinely attempts an acquire rather than being a no-op, and that whatever
    `DataverseServiceClientPolicy.CreateAsync` throws propagates through unchanged); an
    already-canceled `CancellationToken` throws `OperationCanceledException` before any connection
    attempt (proving the token is forwarded to the pool's `AcquireAsync`, not dropped) - this works
    without a live Dataverse connection because `ResourcePool<T>.AcquireAsync`'s capacity-gate wait
    observes cancellation immediately, before any resource creation is attempted.
  - A genuine end-to-end smoke test (facade wrapping a real pool, against a live Dataverse
    environment) has not been run this session - flagged as an open item, same caveat already
    documented for the group-pool path in ADR-0019/0006.
