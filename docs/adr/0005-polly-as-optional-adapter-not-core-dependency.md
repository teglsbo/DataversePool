# ADR-0005: Polly as a separate, optional adapter package — not a core dependency

## Status
Accepted (part of MVP scope)

## Context
We need retry/circuit-breaker behavior around operations on leased clients, and a
signal to the pool when connections are "unhealthy". Polly is the obvious choice for
resilience logic in modern .NET, but it should not be forced on all consumers of
`ConnectionPool.Core`/`ConnectionPool.Dataverse`.

## Decision
- `ConnectionPool.Core` and `ConnectionPool.Dataverse` have **no** Polly dependency.
  They expose only the general hooks: `lease.MarkUnhealthy(Exception)` and
  `pool.HealthChanges` (`IObservable<SlotHealthChanged>`).
- A separate project, `ConnectionPool.Dataverse.Polly`, offers an extension method
  that wires Polly's `OnRetry`/`OnOpened` callbacks to `lease.MarkUnhealthy(...)`:

  ```csharp
  pipeline = new ResiliencePipelineBuilder()
      .AddRetry(...)
      .WithPoolHealthSignal(lease)   // <- our extension
      .Build();
  ```

- Included in MVP (not postponed to v2), because it covers 90% of the expected
  usage pattern with minimal code and keeps the core clean.

## Consequences
- Consumers who do not use Polly incur no additional NuGet dependency.
- Consumers who do use Polly get a one-line integration instead of having to
  detect and implement the `MarkUnhealthy` call correctly themselves.
- Future extension (pool → Polly direction, i.e. proactive circuit opening based on
  `pool.HealthChanges` during systemic failure) is an additive change in the same project,
  not a breaking change to the contract.
