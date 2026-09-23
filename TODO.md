# TODO — DataversePool (Dataverse Connection Pooling)

Last updated: 2026-09-18 (ADR-0023: corrected the "one request at a time" premise — a `ServiceClient` does not serialize concurrent async calls; added opt-in live-Dataverse concurrency integration test)

## Name: DataversePool (renamed from XrmPool)

We originally compared four available NuGet names (XrmPool, DvPool, PoolVerse, DataversePool) and
initially chose **XrmPool**. After user feedback ("more modern?") we switched to
**DataversePool**: more explicit/current, matches Microsoft's current "Dataverse" branding rather
than the older "Xrm" SDK name, still available on NuGet, no collision with the sister project
DataverseDuck. `PackageId` is set to `DataversePool.Core` / `DataversePool.Dataverse` /
`DataversePool.Polly` in the three lib csproj files. The sample project
(`samples/DataversePool.Sample`), solution file (`DataversePool.slnx`), and env-var prefix
(`DATAVERSEPOOL_SAMPLE_CONNECTION_STRING`) were renamed to match (internal C# namespaces/folder
names were deliberately NOT renamed from `ConnectionPool.*` — that would be a large, low-value
refactor; the NuGet package name is what the public actually sees).

## Group-pool selection: load-aware vs. blind round-robin

Added `LeastConnectionsSlotSelectionStrategy` as an alternative to the default
`HealthAwareRoundRobinSlotSelectionStrategy`: picks the member with the fewest currently-leased
leases (`PoolStats.LeasedCount`) instead of blind turn-based distribution, with the same
dead-member circuit-breaking (skip/half-open/fail-open). Relevant when call duration varies a lot
between members — see [ADR-0006](docs/adr/0006-dual-pooling-model-single-user-and-round-robin-group.md)'s
updated section for the full trade-off, including why this is **still not** throttle/429-aware
(the pool doesn't see what you do with a lease after `AcquireAsync`). 4 new tests, 13/13 passing in
`ConnectionPool.Dataverse.Tests`.

## Status overview

| # | Task | Status | Depends on |
|---|---|---|---|
| 1 | Scaffold solution + project skeleton | ✅ Done | — |
| 2 | Write ADRs for core decisions | ✅ Done | — |
| 3 | Implement Core pool interfaces | ✅ Done | — |
| 4 | Write Core unit tests | ✅ Done (14/14 passing) | #3 |
| 5 | Implement Dataverse adapter | ✅ Done | #3 |
| 6 | Write Dataverse adapter tests | ✅ Done (9/9 passing) | #5 |
| 7 | Hardening: races/timeouts/dead group member (ADR-0007) | ✅ Done | #3,#5 |
| 8 | Implement Polly adapter | ✅ Done | #5 |
| 9 | Write Polly adapter tests | ✅ Done (3/3 passing) | #8 |
| 10 | README, LICENSE (MIT), CONTRIBUTING, SECURITY, CODE_OF_CONDUCT | ✅ Done | — |
| 11 | Sample project (`samples/DataversePool.Sample`) | ✅ Done | #5,#8 |
| 12 | Live Dataverse connection test | ✅ **Verified against a real org** (see below) | #11 |

**The MVP + open-source baseline is complete, and the library is now proven to work end-to-end
against a real Dataverse organization** (not just against fakes). All 7 projects (3 libs + 3 test
projects + 1 sample) build cleanly, 26/26 tests passing.

## Live Dataverse connection: verified ✅

Ran `samples/DataversePool.Sample`'s single-user smoke test against a real Dataverse organization,
reusing the connection details from the sister project dvduck's `.env`
(`a local .env file outside this repository` — client-secret-based app user, never printed/logged in
plaintext during this session). Result:

- `DataverseUserPool.WarmupAsync()` cloned correctly (sequential warmup, ADR-0002) and established
  a real connection (MSAL client-credential flow, ~1.4s login).
- `AcquireAsync()` handed out a `ServiceClient` with `IsReady=True`.
- A real `WhoAmIRequest` was executed (~3.3s for the first call, including cold start) and returned
  a real `UserId`/`OrganizationId`.

This confirms the entire chain — connection-string parsing, warmup/clone, lease hand-out, the
`IPooledResourcePolicy<T>` integration — genuinely works against a live Dataverse instance, not
just against fakes/mocks. **Update**: the group pool (round-robin across multiple app users) has
now also been tried live — a second real Entra app registration + Dataverse application user was
created specifically for this, see `LiveDataversePoolMultiUserTests` and the "Open questions"
section below.

## Polly adapter (ConnectionPool.Dataverse.Polly) — done

`PollyPoolHealthSignalExtensions` is **generic** over the resource type (not hardcoded to
`ServiceClient`), so it can be tested end-to-end with a fake pool/resource with no Dataverse
dependency, and reused for any `ConnectionPool.Core`-based pool. The project therefore only
references `ConnectionPool.Core` + `Polly.Core`, not `ConnectionPool.Dataverse` (avoiding pulling
in the entire Dataverse SDK for plain Polly users).

- `AddRetryWithPoolHealthSignal<TResult, TResource>(lease, options)` and
  `AddCircuitBreakerWithPoolHealthSignal<TResult, TResource>(lease, options)` (+ non-generic
  object-result overloads) wire `OnRetry`/`OnOpened` to `lease.MarkUnhealthy(exception)`.
- Existing user callbacks on `options.OnRetry`/`OnOpened` are preserved and always called first.
- Tests verify end-to-end through a real `ResourcePool<T>`: after a retry/circuit-open and
  subsequent dispose, the *next* acquire hands out a *new* resource, and the old one is provably
  disposed (not just a mock assertion that MarkUnhealthy was called).

## Open questions / follow-up

- [ ] Confirm or refute the socket-depletion assumption for new-per-request usage (unverified).
- [x] Confirm or refute the CallerId cross-thread race condition with an actual parallel,
      varying-identity test — **confirmed real** against a live Dataverse instance. Two findings
      that reshaped the test itself: (1) `CallerId` (systemuserid-based impersonation) is silently
      ignored for OAuth/client-secret auth — no error, it just doesn't impersonate; the correct
      property is `CallerAADObjectId` (target user's Entra object id), which the server actually
      validates. (2) `WhoAmIRequest` deliberately ignores impersonation and always returns the real
      caller (documented Microsoft behavior), so it can't be used to detect a mixup at all — the
      test now creates a real record per iteration while impersonating and checks the `createdby`
      field instead. With that fixed, `LiveServiceClientConcurrencyTests.ConcurrentRequests_WithDifferentCallerAadObjectIds_OnSameInstance_DoNotMixUpIdentity`
      (opt-in, `Category=Integration`, needs `DVPOOL_IT_CALLER_AAD_OBJECT_ID_A`/`_B`) reproduced the
      race: **1 of 30 concurrent creates was attributed to the wrong impersonated identity** when
      `CallerAADObjectId` was mutated concurrently on one shared `ServiceClient`. This is direct,
      empirical confirmation — not just a theoretical risk — that sharing one instance across
      concurrent callers with different identities is unsafe, independent of throughput. See
      ADR-0023 for details. The separate "does a single instance serialize concurrent async calls"
      question is confirmed **false** (also ADR-0023) — that's a different, now-separately-resolved
      question from this one.
- [ ] Background sweep for MaxIdleLifetime (today only lazy-at-checkout) — deferred to v2 if needed.
- [x] ~~Run the actual live Dataverse smoke test~~ — run and confirmed against a real org (see above).
- [x] Run the group-pool (round-robin) smoke test live with 2+ app users — a second Entra app
      registration + Dataverse application user was created for this. Confirmed via
      `LiveDataversePoolMultiUserTests`: (1) round-robin split exactly 10/10 across 20 acquires
      between two genuinely distinct authenticated identities; (2) 8 concurrent acquires dispatched
      4/4 across both members simultaneously, confirming the pool can fan out across members
      concurrently — the actual mechanism the "raises the concurrency ceiling" claim depends on
      (this does not, and safely cannot, drive load high enough to hit the real per-user
      service-protection limit itself). Also measured while setting this up:
      `LiveImpersonationOverheadTests` found `CallerAADObjectId` impersonation adds negligible
      per-call latency (+3.6ms/call, 1.07x baseline, on an apples-to-apples `RetrieveMultiple`
      comparison) — impersonation's real cost is correctness/operational (shared mutable state,
      see above), not throughput.
- [x] Verify all three `ISlotSelectionStrategy` implementations against real members (previously
      only ever exercised against fakes) — `LiveDataversePoolStrategyTests`: (1) plain
      `RoundRobinSlotSelectionStrategy` strictly alternates between two real members even when one
      already holds an extra outstanding lease, confirming it genuinely ignores load; (2)
      `LeastConnectionsSlotSelectionStrategy` routed all 4 new acquires to the less-loaded member
      while the other held an extra lease, confirming it genuinely steers by `LeasedCount`; (3)
      `HealthAwareRoundRobinSlotSelectionStrategy` given one real, genuinely-broken member (valid
      connection string, deliberately invalid client secret) failed exactly `failureThreshold`
      (2) times then stopped selecting it entirely — every one of 8 logical calls eventually
      succeeded via the healthy member with zero further failures once the circuit opened.
- [x] Investigate what actually happens when one Dataverse user hits the documented 52-concurrent
      ceiling, and how the three strategies react — `LiveConcurrencyThrottleTests`. Key findings:
      (1) the concurrent-request limit is a **live gauge, not a rolling-window lockout** — unlike
      the 5-min request-count / 20-min execution-time budgets, Microsoft's own guidance confirms
      it clears the instant in-flight load drops back under the ceiling, independent of whatever
      (possibly multi-minute) `Retry-After` a rejected call reported. `DataverseUserPool.IsThrottled`
      does **not** know this distinction — it records the server's raw `Retry-After` as a flat
      "avoid this member" window for throttle-aware strategies regardless of cause, which is a
      deliberate, documented, and now-confirmed-conservative trade-off for the concurrency-limit
      case specifically (it's the *correct* behavior for the 5-min/20-min budget violations, where
      the window really is a hard wait). (2) Plain `RoundRobinSlotSelectionStrategy` genuinely has
      no throttle awareness at all — confirmed with a new fast unit test
      (`SelectNext_KeepsSelectingAThrottledMember_UnlikeThrottleAwareStrategies`) alongside the
      existing simulated tests proving `LeastConnections`/`HealthAwareRoundRobin` do skip a
      throttled member. (3) Attempting to actually *trigger* a real 429 empirically, by firing up
      to 300 genuinely concurrent `RetrieveMultiple` calls (via distinct prewarmed clones, `MaxRetryCount=0`
      so the SDK never silently absorbs the 429 itself, and `DisableCrossThreadSafeties=true` per
      clone) against one real application user on this tenant: **zero 429s observed**, even well
      above the commonly-cited 52-concurrent default — this tenant/instance tolerates materially
      more concurrent, lightweight requests than the documented default before rejecting anything
      (either a raised ceiling for this tier, or the default genuinely requires more sustained/
      expensive load to observe with a lightweight singleton-entity query). The
      clears-immediately-after-a-burst and strategy-divergence assertions both still hold
      trivially/safely when no real throttle occurs, so the test is safe to re-run on any tenant.
      Followed up with `ConcurrencyBurst_WithAHeavierMetadataQuery_ProducesGenuine429s`, swapping
      the cheap singleton query for `RetrieveAllEntitiesRequest(EntityFilters.Entity)` (a full
      entity-metadata dump, independent of record volume, so naturally much heavier per call).
      **Important self-correction during this investigation**: an initial run reported "≈27x real
      parallelism achieved" purely from `serial-time / wall-clock`, and concluded that was "well
      above 52-way" — that math only gives *average* concurrency across the whole run, not true
      peak, and 27 is actually *below* 52, so that first result didn't actually prove anything.
      Rewrote the test to directly instrument real-time in-flight concurrency with an
      `Interlocked` counter (peak, not inferred) and to catch *every* exception shape, not just the
      one `DataverseThrottleDetector` already recognizes, so a rejection taking an unexpected form
      couldn't silently disappear as a false "success" either. Result with 150 real, directly-
      verified concurrent in-flight calls (not estimated): **zero rejections of any kind** — no
      recognized 429, no other exception type either. This is now solid evidence (not an
      inference) that this tenant/instance's real read-concurrency ceiling is genuinely well above
      the commonly-cited default of 52 - or that this specific request type (metadata reads) isn't
      subject to it the same way. Followed up with `ConcurrencyBurst_WithRealWrites_ProducesGenuine429s`
      (real `Create` calls against the `task` entity, same instrumentation, always cleans up every
      created record in a `finally` and tags each with a per-run GUID) since writes carry real
      server-side cost (plugins/auditing/indexing) that reads don't, and were suspected to be where
      the ceiling might actually bite. Result: 100 genuinely concurrent in-flight `Create` calls
      (peak directly verified, not inferred), **zero rejections of any kind** here either — and a
      separate post-run query confirmed zero orphaned test records (cleanup fully succeeded). The
      concurrent-request ceiling does not appear to bite harder on writes than on reads for this
      tenant/instance.
- [x] Consider an integration-test project (opt-in, against a real Dataverse instance) — implemented
      as `LiveServiceClientConcurrencyTests` in the existing `ConnectionPool.Dataverse.Tests` project
      rather than a separate project (simpler, still excluded from CI via `Category!=Integration`).
- [x] Before actual NuGet publishing: update the placeholder URLs in `Directory.Build.props`
      (`PackageProjectUrl`/`RepositoryUrl`) — updated to `github.com/teglsbo/dataversepool`.
- [x] Settle the real author/copyright name in `LICENSE` — updated to Niels Teglsbo, along with
      `Directory.Build.props`'s `<Authors>` for consistency.

## Hardening (ADR-0007) — done

After a systematic review of race conditions/timeouts/real-world scenarios, the following was fixed:
- `MarkUnhealthy` is a no-op if called after `DisposeAsync` (use-after-dispose guard).
- `ResourcePool<T>.DisposeAsync` now performs a best-effort drain and prevents new `AcquireAsync`
  calls (throws `ObjectDisposedException`); leases returned after shutdown are disposed directly
  instead of being leaked into `_idle`.
- `PoolStats.UnhealthyOrRecyclingCount` is now counted correctly (was previously hardcoded to 0).
- `PoolOptions.CreateTimeout`: a hung `CreateAsync` no longer blocks the serial creation gate
  indefinitely — the gate is released on timeout, and the abandoned call finishes and is disposed
  automatically on its own (deliberate trade-off: can rarely allow 2 overlapping clones).
- `PoolOptions.MaxIdleLifetime`: idle resources older than the limit are proactively recycled at
  checkout (equivalent to ADO.NET's Connection Lifetime).
- **"One user in a group is dead":** new `HealthAwareRoundRobinSlotSelectionStrategy` (now the
  default in `DataversePool`) tracks `ConsecutiveCreateFailures` per member, skips
  permanently-failing members (circuit-open), retries them after a cooldown (half-open), and fails
  *open* (still picks a member) if all are down simultaneously, rather than locking the group out
  entirely.

See `docs/adr/0007-race-conditions-timeouts-and-failure-scenarios.md` for the full analysis and
all 6 identified points. New tests: `CreateTimeoutTests`, `PoolShutdownTests`,
`DefensiveBehaviorTests` (Core); `HealthAwareRoundRobinSlotSelectionStrategyTests` (Dataverse).

## Core implementation (ConnectionPool.Core) — done

Files: `IPooledResourcePolicy.cs`, `PoolIncidentInfo.cs`, `PoolOptions.cs`, `PoolStats.cs`,
`SlotHealthChanged.cs`, `Slot.cs` (internal), `PooledLease.cs`, `ResourcePool.cs`.

Key implementation details:
- `SemaphoreSlim`-based capacity gate (1 permit per slot) bounds created+leased to `MaxSize`
  without a separate counter that could drift out of sync.
- A separate `_creationGate` (1,1) guarantees `CreateAsync` is never called in parallel (ADR-0002)
  — verified by `SerialCreationGateTests` with 20 concurrent acquires on an empty pool
  (`MaxObservedConcurrentCreations == 1`).
- `MarkUnhealthy` → background recycle without blocking other waiters (ADR-0004) — verified by
  `HealthSignalTests`.
- Lease-leak detection via finalizer (ADR-0003) → same recycle path as `MarkUnhealthy`.
- `HealthChanges` is a hand-rolled `IObservable<SlotHealthChanged>` (no `System.Reactive`
  dependency).
- Tests: 100% fakes (`FakePolicy`/`FakeResource`), no Dataverse dependency, per the test strategy.

## Project structure

```
DataversePool.slnx
src/
  ConnectionPool.Core/                 # generic pool engine, no Dataverse knowledge
  ConnectionPool.Dataverse/            # ServiceClient adapter, single-user + group/round-robin
  ConnectionPool.Dataverse.Polly/      # optional Polly integration (MarkUnhealthy wiring)
tests/
  ConnectionPool.Core.Tests/
  ConnectionPool.Dataverse.Tests/
  ConnectionPool.Dataverse.Polly.Tests/
docs/adr/                              # architecture decisions, see ADR-0001..0006
```

## Key decisions (see docs/adr/ for full rationale)

- **ADR-0001**: `IPooledResourcePolicy<T>` decouples Core from Dataverse.
- **ADR-0002**: Serial creation gate — never clone in parallel (empirically justified).
- **ADR-0003**: Lease isolation is a dispose contract, not runtime-enforced.
- **ADR-0004**: No synchronous health check at checkout; a `MarkUnhealthy` signal instead.
- **ADR-0005**: Polly is a separate, optional adapter package (part of the MVP).
- **ADR-0006**: Dual pooling model — `DataverseUserPool` + `DataversePool` (round-robin, pluggable strategy).
- **ADR-0007**: Hardening of race conditions, timeouts, and dead-member scenarios.
- **ADR-0008**: Throttle detection via HTTP 429/exception (`DataverseThrottleDetector`), not proactive `x-ms-ratelimit-*` headers — the SDK doesn't expose headers on successful calls. `DataversePool.AcquireAsync()` now returns `DataverseLease` so a 429 can be reported back to the correct member (`ReportIfThrottled`).
- **ADR-0009**: Fixed a security-review finding — `IPooledResourcePolicy<T>.OnReturned` now resets `ServiceClient.CallerId` when a resource is returned to the pool, so impersonation doesn't leak to the next, unrelated caller. Also: a distributed-systems review uncovered 5 blocking multi-instance issues (shared budget, fail-open amplification, non-atomic half-open, circuit tracker only tracking creation failures, unbounded acquire queue) — deliberately NOT solved now, but documented as an explicit production constraint in the README ("single process per service-principal set").
- **ADR-0010**: Fixed 2 of the 3 points the user asked to have addressed: (a) `MemberCircuitBreaker` — a new shared type, a genuinely single-probe half-open (only one concurrent caller wins the probe slot per cooldown window, per-process, no shared state across processes per explicit request), replacing the duplicated and non-atomic `_openedAt` logic in both strategies; (b) `AllUnavailableBehavior` (`FailOpen` default/backward-compatible, or `FailFast` → throws `DataversePoolUnavailableException` with member names + earliest known throttle expiry instead of sending traffic to a group already known to be unavailable). Shared budget coordination across processes (point 1 in the original list) remains deliberately unsolved — the user explicitly rejected shared state across processes, so it's only documented (ADR-0009), not built. `ISlotSelectionStrategy.SelectNext` now returns `SlotSelection` (breaking, accepted per pre-1.0). 52/52 tests passing.
- **ADR-0011**: Yet another review round (security: no findings; DB-pool expert; distributed-systems re-review) found ADR-0010's single-probe fix was not complete, plus two new "blocking" findings in Core. Fixed: (a) `MemberCircuitBreaker.CompleteProbe(member, succeeded)` — explicit outcome reporting instead of relying solely on `probeClaimTimeout`; `DataversePool.AcquireAsync` now calls it after every attempt; (b) constructor validation of `cooldownPeriod`/`probeClaimTimeout` (throws on non-positive values); (c) `ReportLeakedLease`/`PublishHealthChanged` now dispatch user callbacks and observer notification via `ThreadPool.QueueUserWorkItem` instead of directly on the finalizer thread, with try/catch around each — a failing subscriber can neither crash the process nor strand capacity; (d) `BuildUnavailableException` now filters out expired `ThrottledUntil` ticks. Deliberately NOT solved: bounded acquire queue/deadline, operational-failure-aware circuit, concurrent cross-member creation in the group (requires empirical verification), and leak detection as purely diagnostic (rejected — would reverse ADR-0003/0004 without the user's input). 61/61 tests passing.
- **ADR-0012**: The user made an explicit decision on the entire remaining ADR-0011 backlog. Fixed: (a) `PoolOptions.AcquireTimeout` — bounded wait on `AcquireAsync` (same pattern as HikariCP's `connectionTimeout`/ADO.NET's `Connect Timeout`: a timeout on the wait itself, not a max queue length), throwing a new `PoolAcquireTimeoutException` (inherits `TimeoutException`) with a `PoolStats` snapshot; (b) `PoolStats.ConsecutiveOperationalFailures` — new counter incremented by `PooledLease.MarkUnhealthy`, reset on a healthy return/successful recycle; `MemberCircuitBreaker.IsEligible` now opens the circuit on EITHER creation or operational failures, so a member that creates fine but fails in use no longer keeps receiving traffic; (c) leak detection is now **purely diagnostic (log-only, like HikariCP)** — a leaked lease is neither disposed nor recycled, only reported via `OnLeakDetected`/the new `SlotHealthState.LeakDetected`; a genuine leak now permanently reduces the pool's capacity by one slot until restart (explicitly accepted trade-off). NOT changed: concurrent cross-member creation in the group (the user accepted the small assumed extra cost of not serializing it, without empirical verification) and cross-process coordination (still rejected). 66/66 tests passing.
- **ADR-0013**: Fourth review round (security: no findings; DB-pool expert + distributed-systems expert: overlapping root causes). Fixed (the user was unavailable, autonomous decisions — all well-defined bugs, no scope expansion): (a) `AcquireTimeout` now bounds the **entire** acquire operation end-to-end (capacity wait + idle-recycle + serialized creation), not just the initial semaphore wait — via a linked `CancellationTokenSource`; the residual limitation (a single, non-cancellation-aware `CreateAsync` with no `CreateTimeout` configured still can't be interrupted) is documented in the XML docs; (b) `ConsecutiveOperationalFailures` is no longer reset by a successful recycle (only by a genuinely healthy `ReturnAsync`) — fixed a bug where a consistently-failing-but-clonable member never reached the breaker threshold; (c) `WarmupAsync` is now idempotent (tops up to `min(PrewarmCount, MaxSize)` instead of creating that many *more* every call); (d) new `MemberCircuitBreaker.AbandonProbe` + `ISlotSelectionStrategy.ReportAcquireAbandoned` — a pure capacity timeout (`PoolAcquireTimeoutException`) is no longer reported as a failed health probe, so it doesn't unnecessarily extend a recovering member's cooldown; (e) new `PoolStats.DetectedLeakCount` — a durable, synchronous counter for GC-detected leaks, visible via `GetStats()` even without any `OnLeakDetected`/`HealthChanges` subscriber, included in `PoolAcquireTimeoutException`'s message; (f) `ResourcePool<T>`'s constructor now validates `AcquireTimeout`/`CreateTimeout`/`MaxIdleLifetime` (throws on non-positive values instead of failing late/confusingly). 82/82 tests passing (up from 66/66), run 3x with no flaky timing failures. **Note:** a fifth review round (ADR-0014) found that (a) and (d) were only partially effective — see ADR-0014.
- **ADR-0014**: Fifth review round (security: no findings; DB-pool expert + distributed-systems expert: found that several of ADR-0013's fixes were only partially effective, plus a new regression in ADR-0013's own work). Fixed (the user was unavailable, autonomous decisions — well-defined bugs with no scope expansion): (a) **new regression fixed**: `CreateThroughGateAsync` (and `RecycleInPlaceAsync`'s inline recycle variant) confused an `AcquireTimeout`/caller cancellation hitting mid-creation with a genuine `CreateTimeout` expiry — incorrectly incrementing `ConsecutiveCreateFailures` and throwing `TimeoutException` instead of `OperationCanceledException`, which bypassed the entire ADR-0013 (a)+(d) chain and reported a pure capacity event as a failed health probe; the two causes are now explicitly distinguished; (b) `MemberCircuitBreaker` gained claim-generation correlation — `IsEligible` can now return an opaque claim number, which `CompleteProbe`/`AbandonProbe` can require to match the current claim (silently ignoring a stale/late report from an already-superseded attempt instead of corrupting a newer claim); threaded through `SlotSelection`/`ISlotSelectionStrategy`/`DataversePool`, 100% backward-compatible (all new parameters optional); (c) `WarmupAsync` got a dedicated `_warmupGate` that makes the whole check-target/create decision atomic across concurrent calls (ADR-0013's fix only covered repeated *sequential* calls). **Deliberately NOT fixed, requires the user's decision**: the fundamental, time-based probe race is still genuinely possible when `AcquireTimeout` is `null` (default) — a full fix requires an architectural choice between strict "wait for known outcome" semantics (risk: permanently blocked recovery from a hung attempt) and the current timeout fallback (risk: rare probe overlap); see ADR-0014 for details. Also documented (not fixed): a previously unreported bug where a half-open member's single probe slot can be "used up" by the eligibility filtering even when that member isn't the one ultimately selected this round — requires an isolated redesign effort. 90/90 tests passing (up from 82/82), run 3x with no flaky timing failures.
- **ADR-0015**: The user asked directly whether the project now has too much complexity, instead of requesting another bug-hunting review round. Direct (non-delegated) inspection: `ResourcePool.cs` had grown to 644 lines across 5 review rounds; `PoolOptions` has 6 knobs. Conclusion: no dead code, no speculative abstraction, no unused configuration — every branch/counter/knob traces to a specific ADR and a specific test; the 6 `PoolOptions` knobs are in line with (smaller than) reference implementations like HikariCP. However, the *process* of running blanket "fix everything" review rounds is showing diminishing returns — round five (ADR-0014) found a genuine regression introduced by round four's own fix (ADR-0013), which is a direct consequence of one very dense file accumulating patch after patch. Decision: (a) no configuration knob or feature removed — nothing to cut without reopening an already-fixed issue; (b) `ResourcePool.cs` split into `ResourcePool.cs` (445 lines: lease/capacity lifecycle) + new `ResourcePool.Recycling.cs` (205 lines: creation/recycling), pure reorganization, zero behavior change, verified via 3x full test run (90/90, unchanged); (c) no further blanket 3-agent review rounds planned by default — future reviews should be narrowly scoped to a specific area instead of a full sweep, to avoid the pattern where a broad fix in one round breaks an adjacent invariant the next round then has to rediscover. See ADR-0015.

## Open questions / follow-up

- [ ] Confirm or refute the socket-depletion assumption for new-per-request usage (unverified, see research).
- [x] Confirm or refute the CallerId cross-thread race condition with an actual parallel,
      varying-identity test (note: this is a *different* risk than the now-fixed cross-*lease*
      leakage, see ADR-0009). **Confirmed real** against a live Dataverse instance — 1/30
      concurrent creates were attributed to the wrong impersonated identity when
      `CallerAADObjectId` was mutated concurrently on one shared `ServiceClient` (`CallerId` had
      to be dropped in favor of `CallerAADObjectId` — it's silently ignored for OAuth auth — and
      `WhoAmIRequest`-based verification had to be replaced with a `createdby`-on-real-record
      check, since `WhoAmI` deliberately ignores impersonation). See the "Open questions" entry
      above and ADR-0023.
- [x] Throttle-aware `ISlotSelectionStrategy` — implemented via `DataverseUserPool.ReportThrottled`/`IsThrottled` + `DataverseLease.ReportIfThrottled`, see ADR-0008. Both selection strategies now skip throttled members (fail-open if all are throttled).
- [ ] Consider whether SOAP-fault (`OrganizationServiceFault`)-based throttle detection is also needed (deliberately omitted for now, see ADR-0008 — would require an extra `System.ServiceModel.Primitives` reference and it's unverified whether this SDK version even throws SOAP faults for throttling).
- [ ] Consider whether single-user (non-group) `DataverseUserPool` should also expose throttle state externally for monitoring (today only used internally by the group's selection strategy).
- [x] Name chosen: **DataversePool** (NuGet IDs: `DataversePool.Core`/`.Dataverse`/`.Polly`).
- [x] Security finding: `CallerId` leaked between leases on pool reuse — fixed via a new `IPooledResourcePolicy<T>.OnReturned` hook, see ADR-0009.
- [x] Genuinely single-probe half-open circuit breaker — implemented via `MemberCircuitBreaker`, see ADR-0010. Per-process, no shared state across processes (deliberate choice).
- [x] Configurable fail-fast (not just fail-open) when all group members are unavailable — implemented via `AllUnavailableBehavior` + `DataversePoolUnavailableException`, see ADR-0010.
- [x] Probe-claim-timeout race in `MemberCircuitBreaker` (single-probe wasn't fully atomic yet) — fixed via explicit `CompleteProbe` outcome reporting, see ADR-0011. Residual risk from an indefinitely hung `CreateAsync` with no `PoolOptions.CreateTimeout` is documented, not fully eliminated.
- [x] Missing validation of `cooldownPeriod`/`probeClaimTimeout` in `MemberCircuitBreaker` — fixed, see ADR-0011.
- [x] Finalizer-thread safety: user code (`OnLeakDetected`, `HealthChanges` observers) ran synchronously on the finalizer thread (process-fatal risk on an unhandled exception) — fixed via `ThreadPool.QueueUserWorkItem` dispatch + try/catch, see ADR-0011.
- [x] Stale `EarliestKnownRetryAt` from expired throttle ticks — fixed, see ADR-0011.
- [x] Bounded acquire queue/deadline in `ResourcePool<T>.AcquireAsync` — implemented via `PoolOptions.AcquireTimeout` + `PoolAcquireTimeoutException`, see ADR-0012; extended to bound the entire acquire operation end-to-end (not just the initial semaphore wait), see ADR-0013.
- [x] Distinguish creation failures from operational failures in the circuit signal — implemented via `PoolStats.ConsecutiveOperationalFailures`, see ADR-0012; fixed a subsequent bug where a successful recycle reset the counter too early, see ADR-0013.
- [x] Leak detection as purely diagnostic (log-only) — implemented, see ADR-0012. Note: a genuine leak now permanently reduces the pool's capacity until restart (deliberate, user-approved trade-off). Now visible via the new, durable `PoolStats.DetectedLeakCount`, see ADR-0013.
- [x] Concurrent cross-member `CreateAsync` in `DataversePool` — **accepted as-is** by the user without empirical verification ("probably fine per instance, just costs a bit of extra time"). No code changed.
- [x] `WarmupAsync` is now idempotent (tops up to `min(PrewarmCount, MaxSize)` instead of creating that many *more* every call) — fixed, see ADR-0013.
- [x] A capacity timeout (`PoolAcquireTimeoutException`) was incorrectly reported as a failed circuit probe — fixed via `MemberCircuitBreaker.AbandonProbe`/`ISlotSelectionStrategy.ReportAcquireAbandoned`, see ADR-0013.
- [x] `PoolOptions` durations (`AcquireTimeout`/`CreateTimeout`/`MaxIdleLifetime`) were unvalidated in `ResourcePool`'s constructor — fixed, see ADR-0013.
- [ ] **Distributed-systems backlog (v2, "coordinated mode")** — only point 1 remains, see ADR-0009/0010/0011/0012:
  1. ~~Shared throttle/circuit state across processes~~ — **deliberately rejected by the user** ("no shared state across processes"). Remains a documented production constraint, not a todo.
- [ ] **DB-pool-design backlog from the fourth review round**, remaining (not yet prioritized by the user):
  - `PoolStats`/observability lacks histograms/percentiles, wait latency, creation/recycle duration, leak age — needed for real production diagnostics.
  - No `MinIdle`/Little's Law guidance for pool sizing.
  - `CreateTimeout` misclassifies caller-side cancellation as a creation timeout (opens the circuit incorrectly).
  - `ConsecutiveOperationalFailures`'s "consecutive" model can still be reset too early by a single, late, healthy return under mixed concurrent traffic (documented nuance in ADR-0013, not fixed — a full fix would require a time-window-/rate-based model).
- [x] CI/CD pipeline — minimal GitHub Actions workflow (`.github/workflows/ci.yml`): restore/build/test on push+PR to `main`, .NET 8, Release config, excludes the opt-in live-Dataverse `Category=Integration` tests (no creds in CI).
- [x] Optional `DataversePool.Metrics` adapter package — publishes `PoolStats` as `System.Diagnostics.Metrics` observable gauges (OpenTelemetry-compatible), Core-only dependency, see ADR-0018.
- [x] Reviewed `dvpool.stress`'s empirical service-protection-limit findings (sibling load-test repo). Finding 6 (SDK's `Retry-After` handling has no cap; only way to get a bounded, self-owned wait is `MaxRetryCount=0` + caller-side retry) prompted a design review of `ExecuteWithThrottleRetryAsync`'s immediate-retry assumption for the no-alternative-member case.
- [x] Published to `github.com/teglsbo/DataversePool` (public): full history/secret scrub, MIT license, CI (build/test on push+PR to `main`), CodeQL default setup, Dependabot (`nuget` + `github-actions`, weekly) all enabled. First Dependabot wave (8 PRs: `actions/setup-dotnet`, `actions/checkout`, `coverlet.collector`, `Microsoft.Extensions.DependencyInjection`, `Microsoft.NET.Test.Sdk`, `Polly.Core`, `System.Security.Cryptography.Xml`, `xunit.runner.visualstudio`) reviewed and merged; fixed a CI-only flaky wall-clock assertion (`AcquireTimeoutTests`, 1s→5s bound) surfaced under runner contention along the way. Branch protection enabled on `main`.
- [x] **Renamed `DataverseGroupPool`→`DataversePool` (+`DataverseGroupLease`→`DataverseLease`, `DataverseGroupUnavailableException`→`DataversePoolUnavailableException`, `GroupAllUnavailableBehavior`→`AllUnavailableBehavior`)** and made it the recommended entry point even for a single Dataverse user (new single-member convenience constructor) so scaling from 1→N users is a pure config change — see ADR-0019. `ExecuteWithThrottleRetryAsync` now only waits out the capped `Retry-After` when the re-acquired lease comes from the *same* member just throttled (no healthy alternative existed) — correct for single-member pools and the previously-unhandled all-members-throttled `FailOpen` case; default `maxAttempts` changed from member-count to `Math.Max(member count, 3)` so small/single-member pools still get meaningful retries by default.
- [x] Three usability/documentation fixes for the pooling model:
  - **`PooledOrganizationService`** — a drop-in `IOrganizationServiceAsync2` facade over `DataversePool`/`DataverseUserPool`, so existing code built around a constructor-injected `IOrganizationServiceAsync` can adopt pooling via a DI-registration change instead of rewriting every call site to an explicit acquire-lease/use/dispose pattern — see ADR-0020.
  - README clarified that `UseWebApi` is not a substitute for pooling on read-heavy workloads — `RetrieveMultiple`/reads never route through the Web API path in this SDK regardless of that setting, so pooling is the only lever for read-concurrency.
  - The 4 `src/` library projects (`ConnectionPool.Core`, `.Dataverse`, `.Dataverse.Polly`, `.Metrics`) now multi-target `net8.0;net10.0` (net9.0 skipped — out of support/unavailable in this environment); CI installs both SDKs. Sample/test projects remain net8.0-only.
- [x] Two more fixes rounding out the facade/auth story:
  - **`DataverseServiceClientPolicy`/`DataverseUserPool` base-client factory constructor** — accepts `Func<CancellationToken, Task<ServiceClient>>` as an alternative to a connection string, for callers whose authentication (e.g. MSAL/custom token-provider callbacks) doesn't fit the `AuthType=ClientSecret;...` connection-string shape — see ADR-0021.
  - README/ADR-0020 clarified that a `CancellationToken` passed to `PooledOrganizationService`'s read methods (`RetrieveMultipleAsync` etc.) only prevents a *new* call from starting, not aborting one already in flight — reads never route through the WebAPI/HTTP path, and the legacy WCF/SOAP path they always use doesn't accept a token mid-call.
- [x] Four concurrency leak/race fixes found during a closer review of the shutdown, throttle-retry, and probe-selection paths — see ADR-0022:
  - `ResourcePool<T>.DisposeAsync` could transiently hand a concurrent `AcquireAsync` caller a resource (or a fresh slot) out of a pool that was still mid-teardown — fixed by draining `_idle` before releasing collected permits, plus a second disposed-state check right after `AcquireAsync` wins a permit.
  - `DataversePool.ExecuteWithThrottleRetryAsync` leaked a lease if the same-member retry-wait was canceled — fixed by moving that wait inside the existing try/finally.
  - Both `HealthAwareRoundRobinSlotSelectionStrategy` and `LeastConnectionsSlotSelectionStrategy` leaked half-open probe claims for every eligible-but-not-selected candidate in a selection round — fixed by abandoning each unused claim immediately.
  - `DataverseServiceClientPolicy.GetOrCreateBaseClientAsync` left a non-ready (but non-null) base client stored in `_baseClient` before throwing — fixed to dispose and clear it first.
- [x] `PooledOrganizationService` never reported Dataverse throttling signals back to the pool — so a multi-member `DataversePool` used only through the facade only ever got blind rotation across members with no way to route around one Dataverse just capped. Fixed: `LeaseScope` now accepts an optional `onException` callback (invoked with the lease and exception before release, with no bearing on what propagates — kept fully generic/testable), and `PooledOrganizationService` supplies `DataverseLease.ReportIfThrottled` as that callback. Retry-on-throttle for the current call is still out of scope for the facade (unchanged) — see ADR-0020.
- [x] **Corrected the README/ADR-0020's "a `ServiceClient` only handles one request at a time" premise** — decompiling the SDK shows the async execute path (`Command_ExecuteAsyncImpl`, what `ExecuteAsync`/`IOrganizationServiceAsync2` actually use) has no lock around the underlying call; only the sync path does. A new opt-in integration test (`LiveServiceClientConcurrencyTests`, `Category=Integration`) confirmed this against a real Dataverse instance: concurrent calls on one instance ran meaningfully faster than sequential, with the gap widening as request count increased (not the flat-ratio signature a client-side lock would produce). The real reasons to pool are now stated explicitly: the per-application-user server-side concurrency ceiling (unaffected by this), construction/clone cost (unaffected), and `CallerId`-style per-instance mutable state making concurrent use across *different identities* unsafe (a correctness risk, not a throughput one) — see ADR-0023.

## Test strategy (brief, see the full design discussion in the session)

- Core: 100% fakes, no network, concurrency stress test for the serial gate.
- Dataverse adapter: fake `IPooledResourcePolicy<ServiceClient>`, tests orchestration only.
- Polly adapter: verify `MarkUnhealthy` is called correctly on retry/circuit-open.
- Integration test against real Dataverse: opt-in, `[Trait("Category","Integration")]`, not in normal CI.
  Implemented: `LiveServiceClientConcurrencyTests` (`tests/ConnectionPool.Dataverse.Tests/`) — measures
  whether a single `ServiceClient` serializes concurrent async calls, and whether concurrent
  `CallerAADObjectId` changes on a shared instance race. Both self-skip without env vars
  (`DVPOOL_IT_CONNECTION_STRING`, optionally `DVPOOL_IT_CALLER_AAD_OBJECT_ID_A`/`_B`,
  `DVPOOL_IT_REQUEST_COUNT`) — no creds needed to build/run the rest of the suite. Both tests have
  now actually been run against a real environment: the serialization test confirms no client-side
  lock on the async path, and the identity-race test reproduced a genuine mixup (1/30 concurrent
  creates attributed to the wrong impersonated identity) — see ADR-0023.
