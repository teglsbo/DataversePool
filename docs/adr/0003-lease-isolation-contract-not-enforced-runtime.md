# ADR-0003: Non-shared lease is a disposal contract, not runtime-enforced isolation

## Status
Accepted (the leak-tracking behavior mentioned under "Decision" is partially updated by
[ADR-0012](0012-log-only-leak-detection-and-bounded-acquire.md) — leak tracking no longer
evacuates/recovers the slot automatically; see ADR-0012 for why and for the full new behavior. The rest of this
ADR's decision — disposal contract, not runtime-enforced isolation — remains unchanged.)

## Context
`ServiceClient` is not thread-safe when shared across threads with different
`CallerId`. We have not verified this race condition ourselves (0 exceptions in testing),
but we never varied `CallerId` concurrently across threads — so the risk is real,
but unverified by us.

A fully runtime-enforced isolation model (for example a per-call lock check) would add
overhead and complexity to every call on the leased client.

## Decision
Isolation is guaranteed **within the contract**: one lease = exclusive ownership of
the resource until `DisposeAsync()` is called. The pool does not enforce that the user
actually refrains from sharing the reference with other threads — that is the user's responsibility.

In return:
- Leak tracking detects leases that are never disposed (via a finalizer warning; see
  the `LeakTrackingObjectPool` pattern) and evacuates/recovers the slot — inexpensive,
  because re-cloning from a warm base is ~1ms (see ADR-0002).
- `MarkUnhealthy(exception)` gives the user a cheap "emergency signal" if they themselves
  discover that a leased resource lost its connection, without having to wait for a
  full health-check cycle.

## Consequences
- No runtime overhead per call on the underlying `ServiceClient`.
- The contract must be clearly documented in XML docs/README: "never share a
  lease's Resource between threads, always dispose, use MarkUnhealthy when you suspect a fault."
- Open follow-up: if future testing *confirms* actual cross-thread corruption
  (not only theoretical risk), we must reconsider a stricter runtime check.
