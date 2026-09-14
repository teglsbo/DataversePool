# ADR-0004: No synchronous health check at checkout — signal-based instead

## Status
Accepted

## Context
Classic "validate on checkout" (known from certain ADO.NET providers) requires a
network roundtrip per `AcquireAsync()` call to confirm that the resource still
works. That adds latency to *every* lease, even in the normal (healthy) case.

## Decision
The pool does **not** validate synchronously on every checkout. Instead:
1. Each slot has an inexpensive in-memory status (`Idle` / `Unhealthy` / `Recycling`),
   updated asynchronously/event-driven — not by a network call.
2. The user calls `lease.MarkUnhealthy(exception)` when they themselves observe a fault
   on the leased client (for example via the Polly adapter, see ADR-0005) — the pool evacuates
   and recreates the slot in the background, without blocking other acquires.
3. A periodic background check (configurable interval, for example 1–5 min) can proactively
   detect token expiry before use, independently of the checkout flow.

## Consequences
- The normal path (`AcquireAsync` on a healthy slot) has no additional network latency.
- Fault detection is reactive (depends on the user calling `MarkUnhealthy`) rather than
  proactively guaranteed on every lease — accepted tradeoff, because the Polly adapter makes
  this one line of boilerplate for the user (see ADR-0005).
- Must be tested: after `MarkUnhealthy`, verify that a new `AcquireAsync` does not return
  the same (now unhealthy) resource, and that recovery does not block other waiting leases.
