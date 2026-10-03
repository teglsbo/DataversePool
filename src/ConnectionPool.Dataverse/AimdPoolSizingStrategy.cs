using System.Runtime.CompilerServices;
using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>Settings for <see cref="AimdPoolSizingStrategy"/> (autoscaling.md §10.4).</summary>
public sealed class AimdPoolSizingStrategyOptions
{
    /// <summary>Smallest size the strategy will shrink a member to. Default 2.</summary>
    public int MinSize { get; init; } = 2;

    /// <summary>Largest size it will grow a member to. Unset: the member's configured MaxSize.</summary>
    public int? Ceiling { get; init; }

    /// <summary>Multiplier applied to the size on a concurrency throttle. Default 0.5.</summary>
    public double DecreaseFactor { get; init; } = 0.5;

    /// <summary>Added to the size per tick once the cooldown has passed. Default 1.</summary>
    public int IncreaseStep { get; init; } = 1;

    /// <summary>
    /// Minimum quiet time after a decrease before growing again; the actual cooldown is
    /// <c>max(this, the throttle's Retry-After)</c>. Default 30 s.
    /// </summary>
    public TimeSpan CooldownAfterThrottle { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// After a decrease, further throttles are ignored for this long. Throttles already in flight when
    /// the limit was hit arrive as a burst; without this each one would halve the size again and a
    /// single congestion event would collapse the member to <see cref="MinSize"/>. Default 10 s.
    /// </summary>
    public TimeSpan DecreaseHoldoff { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// On a request-count throttle, the request rate is capped at this fraction of what was sent in
    /// the last <see cref="RequestWindow"/>. Default 0.8.
    /// </summary>
    public double RequestRateFactor { get; init; } = 0.8;

    /// <summary>Dataverse's request-count window. Default 5 minutes.</summary>
    public TimeSpan RequestWindow { get; init; } = TimeSpan.FromMinutes(5);

    internal void Validate()
    {
        if (RequestRateFactor is <= 0 or >= 1 || double.IsNaN(RequestRateFactor))
        {
            throw new ArgumentOutOfRangeException(nameof(RequestRateFactor), RequestRateFactor, "Must be between 0 and 1 (exclusive).");
        }

        if (RequestWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestWindow), RequestWindow, "Must be positive.");
        }

        if (MinSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinSize), MinSize, "Must be at least 1.");
        }

        if (Ceiling is { } ceiling && ceiling < MinSize)
        {
            throw new ArgumentOutOfRangeException(nameof(Ceiling), ceiling, "Must be at least MinSize.");
        }

        if (DecreaseFactor is <= 0 or >= 1 || double.IsNaN(DecreaseFactor))
        {
            throw new ArgumentOutOfRangeException(nameof(DecreaseFactor), DecreaseFactor, "Must be between 0 and 1 (exclusive).");
        }

        if (IncreaseStep < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(IncreaseStep), IncreaseStep, "Must be at least 1.");
        }

        if (CooldownAfterThrottle < TimeSpan.Zero || DecreaseHoldoff < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CooldownAfterThrottle), "Durations must not be negative.");
        }
    }
}

/// <summary>
/// Additive-increase / multiplicative-decrease sizing driven by Dataverse throttles (autoscaling.md §9.3).
/// A <see cref="ThrottleReason.ConcurrentRequests"/> throttle multiplies the member's size by
/// <see cref="AimdPoolSizingStrategyOptions.DecreaseFactor"/> immediately; after a cooldown (at least the
/// server's own <c>Retry-After</c>) each tick adds <see cref="AimdPoolSizingStrategyOptions.IncreaseStep"/>
/// up to the ceiling.
///
/// <para>
/// Only the concurrency limit is a concurrency problem. Request-count and execution-time throttles are
/// sliding-window budgets that shrinking the pool does not fix (§2.4), so those, and throttles whose
/// limit could not be decoded, leave the size alone. Not implemented yet: window-budget pacing and
/// BBR-style probing above the ceiling.
/// </para>
/// </summary>
public sealed class AimdPoolSizingStrategy : IPoolSizingStrategy
{
    private sealed class State
    {
        public int Current;
        public int Ceiling;
        public DateTimeOffset CooldownUntil;
        public DateTimeOffset NoDecreaseUntil;
        public readonly Queue<DateTimeOffset> Sent = new();
        public int? RateLimit;
        public int RateLimitDropAbove;
        public DateTimeOffset RateCooldownUntil;
        public DateTimeOffset NoRateDecreaseUntil;
    }

    private readonly AimdPoolSizingStrategyOptions _options;
    private readonly TimeProvider _time;
    private readonly ConditionalWeakTable<DataverseUserPool, State> _state = new();

    public AimdPoolSizingStrategy(AimdPoolSizingStrategyOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new AimdPoolSizingStrategyOptions();
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
        var now = _time.GetUtcNow();
        state.Sent.Enqueue(now);
        while (state.Sent.Count > 0 && now - state.Sent.Peek() >= _options.RequestWindow)
        {
            state.Sent.Dequeue();
        }

        if (outcome.Outcome == PoolSizingOutcomeKind.Throttled && outcome.ThrottleReason == ThrottleReason.RequestCount)
        {
            return OnRequestCountThrottle(state, now, outcome);
        }

        if (outcome.Outcome != PoolSizingOutcomeKind.Throttled || outcome.ThrottleReason != ThrottleReason.ConcurrentRequests)
        {
            return null;
        }

        if (now < state.NoDecreaseUntil)
        {
            return null;
        }

        var cooldown = outcome.RetryAfter is { } retryAfter && retryAfter > _options.CooldownAfterThrottle
            ? retryAfter
            : _options.CooldownAfterThrottle;
        state.NoDecreaseUntil = now + _options.DecreaseHoldoff;
        state.CooldownUntil = now + cooldown;

        var target = Math.Max(_options.MinSize, (int)Math.Round(state.Current * _options.DecreaseFactor, MidpointRounding.AwayFromZero));
        target = Math.Min(target, state.Current);
        state.Current = target;
        return Decide(state);
    }

    // Request-count throttles are a rate over a window: cap the rate (pacer), leave concurrency alone.
    private PoolSizingDecision? OnRequestCountThrottle(State state, DateTimeOffset now, PoolSizingOperationOutcome outcome)
    {
        if (now < state.NoRateDecreaseUntil)
        {
            return null;
        }

        var cooldown = outcome.RetryAfter is { } retryAfter && retryAfter > _options.CooldownAfterThrottle
            ? retryAfter
            : _options.CooldownAfterThrottle;
        state.NoRateDecreaseUntil = now + _options.DecreaseHoldoff;
        state.RateCooldownUntil = now + cooldown;
        var limit = Math.Max(1, (int)(state.Sent.Count * _options.RequestRateFactor));
        state.RateLimit = Math.Min(limit, state.RateLimit ?? int.MaxValue);
        state.RateLimitDropAbove = Math.Max(state.RateLimitDropAbove, state.Sent.Count * 2);
        return Decide(state);
    }

    private PoolSizingDecision Decide(State state) =>
        new(state.Current, state.RateLimit, null, state.RateLimit is null ? null : _options.RequestWindow);

    public PoolSizingDecision? OnTick(DataverseUserPool member, PoolStats currentStats, PoolSizingDecision? lastDecision)
    {
        var state = StateFor(member);
        var now = _time.GetUtcNow();
        var changed = false;
        if (state.RateLimit is { } rate && now >= state.RateCooldownUntil)
        {
            var grown = rate + Math.Max(1, rate / 10);
            state.RateLimit = grown > state.RateLimitDropAbove ? null : grown;
            if (state.RateLimit is null)
            {
                state.RateLimitDropAbove = 0;
            }

            changed = true;
        }

        if (now >= state.CooldownUntil && state.Current < state.Ceiling)
        {
            state.Current = Math.Min(state.Ceiling, state.Current + _options.IncreaseStep);
            changed = true;
        }

        return changed ? Decide(state) : null;
    }

    private State StateFor(DataverseUserPool member) =>
        _state.GetValue(member, m => NewState(m.MaxSize));

    private State NewState(int configuredMaxSize) => new()
    {
        Current = configuredMaxSize,
        Ceiling = Math.Max(_options.MinSize, _options.Ceiling ?? configuredMaxSize),
    };
}
