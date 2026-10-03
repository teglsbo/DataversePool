using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Applies an <see cref="IPoolSizingStrategy"/>'s decisions to a <see cref="DataversePool"/>'s members
/// (autoscaling.md §9.4): feeds it completed operations (<see cref="Record"/>) and a periodic tick, clamps
/// every decision to the configured floor/ceiling, and calls <see cref="DataverseUserPool.SetMaxSize"/>
/// only when the size actually changes. Calls into the strategy are serialized per member.
/// </summary>
internal sealed class PoolSizingController : IOperationOutcomeSink, IDisposable
{
    private sealed class MemberState(int configuredMaxSize)
    {
        public readonly object Gate = new();
        public readonly int ConfiguredMaxSize = configuredMaxSize;
        public PoolSizingDecision? LastDecision;
    }

    private readonly IPoolSizingStrategy _strategy;
    private readonly PoolSizingOptions _options;
    private readonly Dictionary<DataverseUserPool, MemberState> _members = new();
    private readonly ITimer _timer;

    public PoolSizingController(IReadOnlyList<DataverseUserPool> members, PoolSizingOptions options)
    {
        _options = options;
        _strategy = options.Strategy;
        foreach (var member in members)
        {
            var state = new MemberState(member.MaxSize);
            _members[member] = state;
            Apply(member, state, new PoolSizingDecision(_strategy.GetInitialSize(member, state.ConfiguredMaxSize)));
        }

        _timer = options.TimeProvider.CreateTimer(_ => Tick(), null, options.TickInterval, options.TickInterval);
    }

    public void Record(object member, PoolSizingOperationOutcome outcome)
    {
        if (member is not DataverseUserPool pool || !_members.TryGetValue(pool, out var state))
        {
            return;
        }

        lock (state.Gate)
        {
            var withLoad = outcome with { InFlightCount = pool.GetStats().LeasedCount };
            if (_strategy.OnOperationCompleted(pool, withLoad) is { } decision)
            {
                Apply(pool, state, decision);
            }
        }
    }

    internal void Tick()
    {
        foreach (var (member, state) in _members)
        {
            try
            {
                lock (state.Gate)
                {
                    if (_strategy.OnTick(member, member.GetStats(), state.LastDecision) is { } decision)
                    {
                        Apply(member, state, decision);
                    }
                }
            }
            catch
            {
                // A faulty strategy must not stop sizing the other members or crash the timer thread.
            }
        }
    }

    private void Apply(DataverseUserPool member, MemberState state, PoolSizingDecision decision)
    {
        var ceiling = _options.MaxSizeCeiling ?? checked(state.ConfiguredMaxSize * 4);
        var target = Math.Clamp(decision.TargetMaxSize, _options.MinSizeFloor, Math.Max(_options.MinSizeFloor, ceiling));
        state.LastDecision = decision with { TargetMaxSize = target };
        if (target != member.MaxSize)
        {
            member.SetMaxSize(target);
        }
    }

    public void Dispose() => _timer.Dispose();
}
