using System.Runtime.CompilerServices;
using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>Settings for <see cref="GradientPoolSizingStrategy"/> (autoscaling.md §10.5).</summary>
public sealed class GradientPoolSizingStrategyOptions
{
    /// <summary>Smallest size the strategy will use. Default 2.</summary>
    public int Floor { get; init; } = 2;

    /// <summary>Largest size it will grow a member to. Unset: the member's configured MaxSize.</summary>
    public int? Ceiling { get; init; }

    /// <summary>Per-operation successful samples needed in a tick window before it counts. Default 10.</summary>
    public int MinSamples { get; init; } = 10;

    /// <summary>Weight of the new limit versus the current one (0-1]. Lower is smoother. Default 0.2.</summary>
    public double Smoothing { get; init; } = 0.2;

    /// <summary>
    /// How long a per-operation minimum latency is trusted before it is re-learned from the current
    /// window, so a permanently slower backend does not look like permanent congestion. Default 5 min.
    /// </summary>
    public TimeSpan MinRttRefreshInterval { get; init; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (Floor < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Floor), Floor, "Must be at least 1.");
        }

        if (Ceiling is { } ceiling && ceiling < Floor)
        {
            throw new ArgumentOutOfRangeException(nameof(Ceiling), ceiling, "Must be at least Floor.");
        }

        if (MinSamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinSamples), MinSamples, "Must be at least 1.");
        }

        if (Smoothing is <= 0 or > 1 || double.IsNaN(Smoothing))
        {
            throw new ArgumentOutOfRangeException(nameof(Smoothing), Smoothing, "Must be in (0, 1].");
        }

        if (MinRttRefreshInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(MinRttRefreshInterval), MinRttRefreshInterval, "Must be positive.");
        }
    }
}

/// <summary>
/// Latency-gradient sizing (Envoy / Netflix style, autoscaling.md §8-§9.3). Per operation name it
/// tracks the lowest latency seen (<c>minRtt</c>) and the mean latency of the last tick window; the
/// gradient <c>minRtt / sampleRtt</c> (clamped to [0.5, 1]) scales the size down when calls slow
/// down, and a <c>sqrt(size)</c> headroom lets it grow when they do not. The minimum gradient across
/// operations wins, so a slow operation is not masked by a fast one. Only successful calls are
/// sampled. The size never grows when the member was not using most of its current size.
///
/// <para>
/// Latency alone is weak evidence on Dataverse (calls differ by payload and server load), so this is
/// normally combined with <see cref="AimdPoolSizingStrategy"/> in a <see cref="CompositePoolSizingStrategy"/>.
/// </para>
/// </summary>
public sealed class GradientPoolSizingStrategy : IPoolSizingStrategy
{
    private sealed class Window
    {
        public int Count;
        public double SumMs;
        public double MinMs = double.MaxValue;
    }

    private sealed class State
    {
        public double Current;
        public int Ceiling;
        public int MaxInFlight;
        public readonly Dictionary<string, Window> Windows = new();
        public readonly Dictionary<string, (double MinMs, DateTimeOffset LearnedAt)> MinRtt = new();
    }

    private readonly GradientPoolSizingStrategyOptions _options;
    private readonly TimeProvider _time;
    private readonly ConditionalWeakTable<DataverseUserPool, State> _state = new();

    public GradientPoolSizingStrategy(GradientPoolSizingStrategyOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new GradientPoolSizingStrategyOptions();
        _options.Validate();
        _time = timeProvider ?? TimeProvider.System;
    }

    public int GetInitialSize(DataverseUserPool member, int configuredMaxSize)
    {
        _state.AddOrUpdate(member, NewState(configuredMaxSize));
        return configuredMaxSize;
    }

    public PoolSizingDecision? OnOperationCompleted(DataverseUserPool member, PoolSizingOperationOutcome outcome)
    {
        var state = StateFor(member);
        state.MaxInFlight = Math.Max(state.MaxInFlight, outcome.InFlightCount);
        if (outcome.Outcome != PoolSizingOutcomeKind.Success)
        {
            return null;
        }

        if (!state.Windows.TryGetValue(outcome.OperationName, out var window))
        {
            window = state.Windows[outcome.OperationName] = new Window();
        }

        var ms = outcome.Duration.TotalMilliseconds;
        window.Count++;
        window.SumMs += ms;
        window.MinMs = Math.Min(window.MinMs, ms);
        return null;
    }

    public PoolSizingDecision? OnTick(DataverseUserPool member, PoolStats currentStats, PoolSizingDecision? lastDecision)
    {
        var state = StateFor(member);
        var now = _time.GetUtcNow();
        var gradient = 1.0;
        var sampled = false;

        foreach (var (operation, window) in state.Windows)
        {
            if (window.Count < _options.MinSamples)
            {
                continue;
            }

            if (!state.MinRtt.TryGetValue(operation, out var known) || now - known.LearnedAt >= _options.MinRttRefreshInterval)
            {
                known = (window.MinMs, now);
            }
            else if (window.MinMs < known.MinMs)
            {
                known = (window.MinMs, known.LearnedAt);
            }

            state.MinRtt[operation] = known;
            var sampleRtt = window.SumMs / window.Count;
            if (sampleRtt > 0)
            {
                gradient = Math.Min(gradient, Math.Clamp(known.MinMs / sampleRtt, 0.5, 1.0));
                sampled = true;
            }
        }

        var maxInFlight = state.MaxInFlight;
        state.Windows.Clear();
        state.MaxInFlight = 0;
        if (!sampled)
        {
            return null;
        }

        var raw = gradient * state.Current + Math.Sqrt(state.Current);
        if (maxInFlight < state.Current * 0.5)
        {
            raw = Math.Min(raw, state.Current); // idle member: latency says nothing about capacity
        }

        var next = state.Current * (1 - _options.Smoothing) + raw * _options.Smoothing;
        next = Math.Clamp(next, _options.Floor, state.Ceiling);
        state.Current = next;
        return new PoolSizingDecision((int)Math.Round(next, MidpointRounding.AwayFromZero));
    }

    private State StateFor(DataverseUserPool member) =>
        _state.GetValue(member, m => NewState(m.MaxSize));

    private State NewState(int configuredMaxSize) => new()
    {
        Current = configuredMaxSize,
        Ceiling = Math.Max(_options.Floor, _options.Ceiling ?? configuredMaxSize),
    };
}
