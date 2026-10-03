using System.Runtime.CompilerServices;
using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>Settings for <see cref="DopHintPoolSizingStrategy"/> (autoscaling.md §10.3).</summary>
public sealed class DopHintPoolSizingStrategyOptions
{
    /// <summary>Scales the observed hint before clamping. Default 1.0.</summary>
    public double Multiplier { get; init; } = 1.0;

    /// <summary>Smallest size the hint may drive a member to. Default 2.</summary>
    public int Floor { get; init; } = 2;

    /// <summary>Largest size the hint may drive a member to. Unset: the member's configured MaxSize.</summary>
    public int? Ceiling { get; init; }

    internal void Validate()
    {
        if (Multiplier <= 0 || double.IsNaN(Multiplier) || double.IsInfinity(Multiplier))
        {
            throw new ArgumentOutOfRangeException(nameof(Multiplier), Multiplier, "Must be a positive, finite number.");
        }

        if (Floor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Floor), Floor, "Must be at least 1.");
        }

        if (Ceiling is { } ceiling && ceiling < Floor)
        {
            throw new ArgumentOutOfRangeException(nameof(Ceiling), ceiling, "Must be at least Floor.");
        }
    }
}

/// <summary>
/// Follows Dataverse's own <c>x-ms-dop-hint</c> (<see cref="DataverseUserPool.RecommendedDegreesOfParallelism"/>):
/// on every tick, the target is <c>clamp(round(hint * Multiplier), Floor, Ceiling)</c>. Does not react to
/// throttles or latency. Until a hint has been observed it has no opinion and the member keeps its
/// configured size.
/// </summary>
public sealed class DopHintPoolSizingStrategy : IPoolSizingStrategy
{
    private readonly DopHintPoolSizingStrategyOptions _options;
    private readonly ConditionalWeakTable<DataverseUserPool, StrongBox<int>> _configured = new();

    public DopHintPoolSizingStrategy(DopHintPoolSizingStrategyOptions? options = null)
    {
        _options = options ?? new DopHintPoolSizingStrategyOptions();
        _options.Validate();
    }

    public int GetInitialSize(DataverseUserPool member, int configuredMaxSize)
    {
        _configured.AddOrUpdate(member, new StrongBox<int>(configuredMaxSize));
        return configuredMaxSize;
    }

    public PoolSizingDecision? OnTick(DataverseUserPool member, PoolStats currentStats, PoolSizingDecision? lastDecision)
    {
        if (member.RecommendedDegreesOfParallelism is not { } hint || hint <= 0)
        {
            return null;
        }

        var configured = _configured.TryGetValue(member, out var box) ? box.Value : member.MaxSize;
        var ceiling = Math.Max(_options.Floor, _options.Ceiling ?? configured);
        var target = (int)Math.Round(hint * _options.Multiplier, MidpointRounding.AwayFromZero);
        return new PoolSizingDecision(Math.Clamp(target, _options.Floor, ceiling));
    }
}
