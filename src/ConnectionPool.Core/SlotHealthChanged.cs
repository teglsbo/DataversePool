namespace ConnectionPool.Core;

public enum SlotHealthState
{
    MarkedUnhealthy,
    RecyclingStarted,
    Recovered,
    RecoveryFailed,
    LeakDetected,
}

/// <summary>
/// Emitted via <see cref="ResourcePool{T}.HealthChanges"/> whenever a slot transitions health state.
/// Consumers (e.g. the optional Polly adapter, or custom monitoring) can subscribe to this to react to
/// systemic problems without the pool taking a hard dependency on any specific resilience library.
/// See docs/adr/0005-polly-as-optional-adapter-not-core-dependency.md.
/// </summary>
public sealed record SlotHealthChanged(SlotHealthState State, PoolIncidentInfo? Incident);
