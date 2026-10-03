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
        public MemberRequestPacer Pacer = null!;
        public ResponseBudget? Budget;
        public double MaxBurstSeen;
        public double MaxTimeSeen;
        public readonly Dictionary<string, ServerNodeStats> Nodes = new();
    }

    private readonly IPoolSizingStrategy _strategy;
    private readonly PoolSizingOptions _options;
    private readonly Dictionary<DataverseUserPool, MemberState> _members = new();
    private readonly ITimer _timer;
    private readonly IDisposable? _observer;

    public PoolSizingController(IReadOnlyList<DataverseUserPool> members, PoolSizingOptions options)
    {
        _options = options;
        _strategy = options.Strategy;
        foreach (var member in members)
        {
            var state = new MemberState(member.MaxSize) { Pacer = new MemberRequestPacer(options.TimeProvider) };
            _members[member] = state;
            Apply(member, state, new PoolSizingDecision(_strategy.GetInitialSize(member, state.ConfiguredMaxSize)));
        }

        if (options.ObserveResponses)
        {
            _observer = ResponseObservation.Start();
        }

        _timer = options.TimeProvider.CreateTimer(_ => Tick(), null, options.TickInterval, options.TickInterval);
    }

    public bool ObservesResponses => _options.ObserveResponses;

    public ResponseBudget? GetBudget(DataverseUserPool member) =>
        _members.TryGetValue(member, out var state) ? Volatile.Read(ref state.Budget) : null;

    private const int MaxTrackedNodes = 256;

    private static double? Min(double? a, double? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    public IReadOnlyList<ServerNodeStats> GetServerStats(DataverseUserPool member)
    {
        if (!_members.TryGetValue(member, out var state))
        {
            return [];
        }

        lock (state.Gate)
        {
            return state.Nodes.Values.OrderBy(n => n.ServerId, StringComparer.Ordinal).ToArray();
        }
    }

    public void OnResponse(object member, ResponseBudget budget)
    {
        if (member is not DataverseUserPool pool || !_members.TryGetValue(pool, out var state))
        {
            return;
        }

        budget = budget with { ObservedAt = _options.TimeProvider.GetUtcNow() };
        Volatile.Write(ref state.Budget, budget);
        var low = false;
        lock (state.Gate)
        {
            if (budget.ServerId is { } node && (state.Nodes.ContainsKey(node) || state.Nodes.Count < MaxTrackedNodes))
            {
                var burstNow = budget.BurstRemainingRequests;
                state.Nodes[node] = state.Nodes.TryGetValue(node, out var prev)
                    ? new ServerNodeStats(node, prev.Responses + 1, Min(prev.MinBurst, burstNow), Max(prev.MaxBurst, burstNow))
                    : new ServerNodeStats(node, 1, burstNow, burstNow);
            }

            if (budget.BurstRemainingRequests is { } burst)
            {
                state.MaxBurstSeen = Math.Max(state.MaxBurstSeen, burst);
                low |= burst <= state.MaxBurstSeen * _options.LowBudgetFraction;
            }

            if (budget.TimeRemainingSeconds is { } seconds)
            {
                state.MaxTimeSeen = Math.Max(state.MaxTimeSeen, seconds);
                low |= seconds <= state.MaxTimeSeen * _options.LowBudgetFraction;
            }
        }

        if (low)
        {
            state.Pacer.HoldUntil(budget.ObservedAt + _options.LowBudgetBackoff);
        }
    }

    public ValueTask BeforeAttemptAsync(object member, CancellationToken cancellationToken) =>
        member is DataverseUserPool pool && _members.TryGetValue(pool, out var state)
            ? state.Pacer.WaitAsync(cancellationToken)
            : ValueTask.CompletedTask;

    public void Record(object member, PoolSizingOperationOutcome outcome)
    {
        if (member is not DataverseUserPool pool || !_members.TryGetValue(pool, out var state))
        {
            return;
        }

        if (outcome.Outcome is PoolSizingOutcomeKind.Success or PoolSizingOutcomeKind.Error)
        {
            state.Pacer.RecordExecution(outcome.Duration);
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
        state.Pacer.Configure(decision.MaxRequestsPerWindow, decision.SampleWindow, decision.MaxExecutionTimePerWindow);
        if (target != member.MaxSize)
        {
            member.SetMaxSize(target);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _observer?.Dispose();
    }
}
