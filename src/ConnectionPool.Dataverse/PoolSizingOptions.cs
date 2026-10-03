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
