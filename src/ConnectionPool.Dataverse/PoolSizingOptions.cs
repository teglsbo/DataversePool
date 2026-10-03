namespace ConnectionPool.Dataverse;

/// <summary>
/// Opt-in automatic sizing for a <see cref="DataversePool"/> (docs/research/autoscaling.md §9-§10).
/// Passing no options (or <see cref="FixedPoolSizingStrategy"/>) keeps every member's
/// <see cref="ConnectionPool.Core.PoolOptions.MaxSize"/> static, exactly as before.
/// </summary>
public sealed class PoolSizingOptions
{
    /// <summary>The sizing strategy (or a <see cref="CompositePoolSizingStrategy"/>). Default: fixed sizes.</summary>
    public IPoolSizingStrategy Strategy { get; init; } = new FixedPoolSizingStrategy();

    /// <summary>How often <see cref="IPoolSizingStrategy.OnTick"/> runs. Default 10 s.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Hard floor for any applied size, whatever the strategy returns. Default 1.</summary>
    public int MinSizeFloor { get; init; } = 1;

    /// <summary>
    /// Hard ceiling for any applied size. Unset: 4x the member's configured
    /// <see cref="ConnectionPool.Core.PoolOptions.MaxSize"/>.
    /// </summary>
    public int? MaxSizeCeiling { get; init; }

    /// <summary>
    /// EXPERIMENTAL. Read Dataverse's <c>x-ms-ratelimit-*</c> budget headers from the SDK's HTTP
    /// responses (<see cref="DataversePool.GetResponseBudget"/>) and briefly hold back new attempts on a
    /// member whose remaining request or execution-time budget is nearly spent, before Dataverse has to
    /// throttle it. Works with any strategy, including the default. Default: off.
    /// </summary>
    public bool ObserveResponses { get; init; }

    /// <summary>
    /// With <see cref="ObserveResponses"/>: hold attempts back when the remaining budget falls to this
    /// fraction of the largest value seen for the member. Default 0.05.
    /// </summary>
    public double LowBudgetFraction { get; init; } = 0.05;

    /// <summary>
    /// With <see cref="ObserveResponses"/>: how long a low budget holds attempts back before one is let
    /// through to refresh the reading (fail-open). Default 5 s.
    /// </summary>
    public TimeSpan LowBudgetBackoff { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Test seam for the tick timer and strategy clocks.</summary>
    internal TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Throws if a value is out of range.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Strategy);
        if (TickInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(TickInterval), TickInterval, "Must be a positive duration.");
        }

        if (LowBudgetFraction is <= 0 or >= 1 || double.IsNaN(LowBudgetFraction))
        {
            throw new ArgumentOutOfRangeException(nameof(LowBudgetFraction), LowBudgetFraction, "Must be between 0 and 1 (exclusive).");
        }

        if (LowBudgetBackoff <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LowBudgetBackoff), LowBudgetBackoff, "Must be a positive duration.");
        }

        if (MinSizeFloor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinSizeFloor), MinSizeFloor, "Must be at least 1.");
        }

        if (MaxSizeCeiling is { } ceiling && ceiling < MinSizeFloor)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxSizeCeiling), ceiling, "Must be at least MinSizeFloor.");
        }
    }
}
