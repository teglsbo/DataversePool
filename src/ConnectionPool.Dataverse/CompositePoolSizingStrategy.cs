using System.Runtime.CompilerServices;
using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>How a <see cref="CompositePoolSizingStrategy"/> combines its children's sizes.</summary>
public enum PoolSizingCombineMode
{
    /// <summary>The smallest child size wins (safety-first; the default).</summary>
    Min,

    /// <summary>The largest child size wins.</summary>
    Max,
}

/// <summary>
/// Combines several strategies (e.g. <see cref="DopHintPoolSizingStrategy"/> + <see cref="AimdPoolSizingStrategy"/>).
/// It remembers each child's <i>latest</i> size per member, so a child that has no opinion on a given
/// call keeps its previous vote instead of letting another child's decision override it.
/// </summary>
public sealed class CompositePoolSizingStrategy : IPoolSizingStrategy
{
    private readonly IPoolSizingStrategy[] _children;
    private readonly PoolSizingCombineMode _mode;
    private readonly ConditionalWeakTable<DataverseUserPool, int[]> _votes = new();
    private readonly ConditionalWeakTable<DataverseUserPool, PoolSizingDecision?[]> _lastChildDecisions = new();

    public CompositePoolSizingStrategy(IEnumerable<IPoolSizingStrategy> children, PoolSizingCombineMode mode = PoolSizingCombineMode.Min)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children = children.ToArray();
        if (_children.Length == 0 || _children.Any(c => c is null))
        {
            throw new ArgumentException("Provide at least one non-null child strategy.", nameof(children));
        }

        _mode = mode;
    }

    public CompositePoolSizingStrategy(params IPoolSizingStrategy[] children)
        : this((IEnumerable<IPoolSizingStrategy>)children)
    {
    }

    public int GetInitialSize(DataverseUserPool member, int configuredMaxSize)
    {
        var votes = _children.Select(c => c.GetInitialSize(member, configuredMaxSize)).ToArray();
        _votes.AddOrUpdate(member, votes);
        _lastChildDecisions.AddOrUpdate(member, new PoolSizingDecision?[_children.Length]);
        return Combine(votes);
    }

    public PoolSizingDecision? OnOperationCompleted(DataverseUserPool member, PoolSizingOperationOutcome outcome)
    {
        var votes = VotesFor(member);
        var lasts = LastsFor(member);
        var changed = false;
        for (var i = 0; i < _children.Length; i++)
        {
            if (_children[i].OnOperationCompleted(member, outcome) is { } decision)
            {
                votes[i] = decision.TargetMaxSize;
                lasts[i] = decision;
                changed = true;
            }
        }

        return changed ? Build(votes, lasts) : null;
    }

    public PoolSizingDecision? OnTick(DataverseUserPool member, PoolStats currentStats, PoolSizingDecision? lastDecision)
    {
        var votes = VotesFor(member);
        var lasts = LastsFor(member);
        var changed = false;
        for (var i = 0; i < _children.Length; i++)
        {
            if (_children[i].OnTick(member, currentStats, lasts[i]) is { } decision)
            {
                votes[i] = decision.TargetMaxSize;
                lasts[i] = decision;
                changed = true;
            }
        }

        return changed ? Build(votes, lasts) : null;
    }

    // Pacing is a safety limit, so the tightest child limit wins regardless of the size combine mode.
    private PoolSizingDecision Build(int[] votes, PoolSizingDecision?[] lasts)
    {
        PoolSizingDecision? tightest = null;
        foreach (var last in lasts)
        {
            if (last is { MaxRequestsPerWindow: { } limit } d && (tightest is null || limit < tightest.Value.MaxRequestsPerWindow))
            {
                tightest = d;
            }
        }

        TimeSpan? execution = null;
        foreach (var last in lasts)
        {
            if (last?.MaxExecutionTimePerWindow is { } e && (execution is null || e < execution))
            {
                execution = e;
                tightest ??= last;
            }
        }

        return new PoolSizingDecision(Combine(votes), tightest?.MaxRequestsPerWindow, execution, tightest?.SampleWindow);
    }

    private int Combine(int[] votes) => _mode == PoolSizingCombineMode.Min ? votes.Min() : votes.Max();

    private int[] VotesFor(DataverseUserPool member) =>
        _votes.GetValue(member, m => Enumerable.Repeat(m.MaxSize, _children.Length).ToArray());

    private PoolSizingDecision?[] LastsFor(DataverseUserPool member) =>
        _lastChildDecisions.GetValue(member, _ => new PoolSizingDecision?[_children.Length]);
}
