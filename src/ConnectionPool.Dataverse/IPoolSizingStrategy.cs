using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Pluggable strategy for deciding each member's <see cref="DataverseUserPool.MaxSize"/> at runtime
/// (docs/research/autoscaling.md §9). Like <see cref="ISlotSelectionStrategy"/> it is a pure decision
/// interface: it returns a <see cref="PoolSizingDecision"/> and never calls
/// <see cref="DataverseUserPool.SetMaxSize"/> itself - the pool's sizing controller applies decisions
/// (clamped to <see cref="PoolSizingOptions.MinSizeFloor"/>/<see cref="PoolSizingOptions.MaxSizeCeiling"/>).
///
/// <para>
/// One strategy instance serves every member of a <see cref="DataversePool"/>, so implementations that
/// keep state must key it by member. The controller serializes all calls for a given member, so
/// per-member state needs no locking of its own.
/// </para>
/// </summary>
public interface IPoolSizingStrategy
{
    /// <summary>
    /// Seeds <paramref name="member"/>'s initial size when the pool starts sizing it.
    /// <paramref name="configuredMaxSize"/> is the member's <see cref="PoolOptions.MaxSize"/>; most
    /// strategies return it unchanged.
    /// </summary>
    int GetInitialSize(DataverseUserPool member, int configuredMaxSize);

    /// <summary>
    /// Reports one completed operation attempt against <paramref name="member"/> (the same attempts
    /// ADR-0024 instruments). Event-driven: a loss-based strategy reacts here, immediately. Return a
    /// decision to change the size now, or <c>null</c> for no opinion. Must be cheap - it runs on the
    /// caller's completion path.
    /// </summary>
    PoolSizingDecision? OnOperationCompleted(DataverseUserPool member, PoolSizingOperationOutcome outcome) => null;

    /// <summary>
    /// Called every <see cref="PoolSizingOptions.TickInterval"/> for every member, for slower signals
    /// (a DOP-hint re-seed, additive increase after a cooldown). <paramref name="lastDecision"/> is
    /// what this strategy last returned for the member; <paramref name="currentStats"/> lets it see
    /// whether an earlier shrink has taken effect yet (shrinking is graceful, ADR-0025).
    /// </summary>
    PoolSizingDecision? OnTick(DataverseUserPool member, PoolStats currentStats, PoolSizingDecision? lastDecision) => null;
}

/// <summary>
/// One completed operation attempt as seen by an <see cref="IPoolSizingStrategy"/>.
/// </summary>
/// <param name="OperationName">Low-cardinality operation label, as in ADR-0024.</param>
/// <param name="Duration">Wall-clock duration of the attempt, excluding lease-acquire wait.</param>
/// <param name="Outcome">Success, error, throttled or canceled.</param>
/// <param name="ThrottleReason">Which limit fired; set only when <paramref name="Outcome"/> is
/// <see cref="PoolSizingOutcomeKind.Throttled"/>.</param>
/// <param name="RetryAfter">The (capped) server back-off for a throttled attempt.</param>
/// <param name="InFlightCount">Leases the member had out when the attempt completed.</param>
public readonly record struct PoolSizingOperationOutcome(
    string OperationName,
    TimeSpan Duration,
    PoolSizingOutcomeKind Outcome,
    ThrottleReason? ThrottleReason,
    TimeSpan? RetryAfter,
    int InFlightCount);

/// <summary>Mirrors ADR-0024's <c>outcome</c> tag values.</summary>
public enum PoolSizingOutcomeKind { Success, Error, Throttled, Canceled }

/// <summary>Which Dataverse service-protection limit a throttle reports (autoscaling.md §2).</summary>
public enum ThrottleReason { RequestCount, ExecutionTime, ConcurrentRequests, Unknown }

/// <summary>
/// A strategy's output: the target <see cref="DataverseUserPool.MaxSize"/> for one member. The three
/// pacing fields are reserved for the two sliding-window limits a concurrency count cannot fix
/// (request count and execution time); no pacer consumes them yet, so the controller ignores them.
/// </summary>
public readonly record struct PoolSizingDecision(
    int TargetMaxSize,
    int? MaxRequestsPerWindow = null,
    TimeSpan? MaxExecutionTimePerWindow = null,
    TimeSpan? SampleWindow = null);
