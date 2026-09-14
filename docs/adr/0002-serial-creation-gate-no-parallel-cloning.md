# ADR-0002: Serial creation gate — no parallel cloning of resources

## Status
Accepted

## Context
Empirical measurement (the earlier work in this session) showed that `ServiceClient` cloning
is bimodal:
- First clone from a base connection: ~500ms (real auth/network).
- Subsequent sequential clones: ~1ms (cached token/discovery is reused).
- Parallel clones (several at the same time): 1–3.2s per clone due to internal lock contention
  in the SDK's own creation code.

This means that "prewarm in parallel for faster startup" is counterproductive and can make
startup significantly slower than sequential creation.

## Decision
`ResourcePool<T>` guarantees that `policy.CreateAsync()` is **never called simultaneously from two
threads** for the same underlying base connection — enforced with an internal
`SemaphoreSlim(1,1)` ("creation gate") around all calls to `CreateAsync`.

This applies regardless of trigger:
- Eager/sequential prewarm at startup (via `IHostedService`).
- Lazy creation on `AcquireAsync()` when the pool is empty.
- Recovery of a slot after `MarkUnhealthy`.

Prewarm-at-startup is **not** a hard requirement in v1 (lazy is acceptable), but the
serial gate is a hard requirement regardless of creation strategy.

## Consequences
- Simple implementation: one global (or per-base-connection) semaphore, no
  complex scheduling.
- Possible wait time during cold start under high concurrent load (multiple callers wait on
  the same gate), accepted as a tradeoff against avoiding a 1–3.2s lock-contention cost per clone.
- Must be verified with a concurrency stress test (see test strategy) that counts
  simultaneous `CreateAsync` calls via `Interlocked` and asserts max == 1.
