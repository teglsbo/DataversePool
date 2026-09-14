# ADR-0006: Dual pooling model — single-user pool and round-robin group pool

## Status
Accepted

## Context
Dataverse enforces service protection limits per user/app (~52 concurrent calls).
Some consumers have only one service user and want simple pooling of multiple
`ServiceClient` instances for that one user. Others have multiple app users and want
to scale beyond a single user's ceiling by distributing load across them (round-robin), with
room to later make the distribution throttle-aware (based on `x-ms-dop-hint`
and 429/budget signals).

## Decision
Two public types in `ConnectionPool.Dataverse`:

- `DataverseUserPool` — pool of `ServiceClient` instances for **one** user/connection string.
- `DataverseGroupPool` — composes multiple `DataverseUserPool` instances and chooses
  which one to use via a pluggable strategy:

  ```csharp
  public interface ISlotSelectionStrategy
  {
      DataverseUserPool SelectNext(IReadOnlyList<DataverseUserPool> members, PoolStats[] stats);
  }
  ```

  v1 provides only a simple `RoundRobinSlotSelectionStrategy`. The interface is designed
  so it can later receive a `ThrottleAwareSlotSelectionStrategy` that avoids
  members close to their budget/`dop-hint` ceiling, without changing the public API of
  `DataverseGroupPool`.

## Consequences
- Consumers with a simple need (one user) use `DataverseUserPool` directly, without
  overhead from the group layer.
- Group logic is isolated in the strategy — round-robin in v1 can be replaced without
  changing the public contract of `DataverseGroupPool`.
- Requires that `PoolStats` (in-use/wait-time/unhealthy-count) is available per
  member pool, so a future throttle-aware strategy has data to choose from.

## Update: load-based choice vs. blind round-robin

After v1, the standard strategy has been changed to `HealthAwareRoundRobinSlotSelectionStrategy` (see ADR-0007
#6), and a `LeastConnectionsSlotSelectionStrategy` has also been added as an alternative — it
selects the member with the fewest currently leased-out leases (`PoolStats.LeasedCount`) rather than blind
turn-based ordering, with the same circuit breaking (dead-member skip/half-open/fail-open) as the
health-aware variant.

**Why this is still not "throttle-aware":** `LeasedCount` measures only *how many leases are
checked out right now* — not how close a member actually is to its ~52-concurrent ceiling, and certainly not
whether Dataverse is already sending 429/`Retry-After` on requests executed through a leased
`ServiceClient`. The pool has no visibility into what a consumer does with a lease after
`AcquireAsync()` returns it, so truly throttle-aware selection (based on `x-ms-dop-hint` or
observed 429s) requires the consumer to report request-level outcomes back to the pool —
a larger API extension than merely a new `ISlotSelectionStrategy` implementation. That remains
future scope and was not done in this round.

**When to choose which:**
- `HealthAwareRoundRobinSlotSelectionStrategy` (default): simplest, even distribution, prefer
  when members typically have similar request duration/time.
- `LeastConnectionsSlotSelectionStrategy`: prefer when calls have highly variable duration (some
  members may be stuck in long-running calls), so new acquires do not just continue piling up
  on an already stressed member.
