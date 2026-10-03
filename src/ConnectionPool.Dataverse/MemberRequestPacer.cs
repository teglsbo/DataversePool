namespace ConnectionPool.Dataverse;

/// <summary>
/// Sliding-window request limiter for one member (the request-count service protection limit is a
/// rate over a 5-minute window, which a concurrency cap cannot enforce). With no limit set it is a
/// no-op. Waiting callers hold no lock; cancellation aborts the wait.
/// </summary>
internal sealed class MemberRequestPacer(TimeProvider time)
{
    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _starts = new();
    private readonly Queue<(DateTimeOffset At, TimeSpan Duration)> _executions = new();
    private TimeSpan _executionSum;
    private TimeSpan? _executionLimit;
    private DateTimeOffset _holdUntil;
    private int? _limit;
    private TimeSpan _window = TimeSpan.FromMinutes(5);

    public (int? Limit, TimeSpan Window) Current
    {
        get { lock (_gate) { return (_limit, _window); } }
    }

    /// <summary>Delays every new attempt until <paramref name="until"/> (a fail-open, time-bounded gate).</summary>
    public void HoldUntil(DateTimeOffset until)
    {
        lock (_gate)
        {
            if (until > _holdUntil)
            {
                _holdUntil = until;
            }
        }
    }

    /// <summary>Adds a finished attempt's duration to the execution-time budget window.</summary>
    public void RecordExecution(TimeSpan duration)
    {
        lock (_gate)
        {
            if (_executionLimit is not null)
            {
                _executions.Enqueue((time.GetUtcNow(), duration));
                _executionSum += duration;
            }
        }
    }

    public void Configure(int? maxRequestsPerWindow, TimeSpan? window, TimeSpan? maxExecutionTimePerWindow = null)
    {
        lock (_gate)
        {
            _executionLimit = maxExecutionTimePerWindow is { } e ? (e > TimeSpan.Zero ? e : TimeSpan.FromMilliseconds(1)) : null;
            if (_executionLimit is null)
            {
                _executions.Clear();
                _executionSum = TimeSpan.Zero;
            }

            _limit = maxRequestsPerWindow is { } l ? Math.Max(1, l) : null;
            if (window is { } w && w > TimeSpan.Zero)
            {
                _window = w;
            }
        }
    }

    public async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;
            lock (_gate)
            {
                var now = time.GetUtcNow();
                if (now < _holdUntil)
                {
                    delay = _holdUntil - now;
                    goto wait;
                }

                if (_limit is null && _executionLimit is null)
                {
                    return;
                }

                while (_starts.Count > 0 && now - _starts.Peek() >= _window)
                {
                    _starts.Dequeue();
                }

                while (_executions.Count > 0 && now - _executions.Peek().At >= _window)
                {
                    _executionSum -= _executions.Dequeue().Duration;
                }

                var countBlocked = _limit is { } limit && _starts.Count >= limit;
                var timeBlocked = _executionLimit is { } budget && _executionSum >= budget && _executions.Count > 0;
                if (!countBlocked && !timeBlocked)
                {
                    _starts.Enqueue(now);
                    return;
                }

                delay = TimeSpan.Zero;
                if (countBlocked)
                {
                    delay = _starts.Peek() + _window - now;
                }

                if (timeBlocked)
                {
                    delay = TimeSpan.FromTicks(Math.Max(delay.Ticks, (_executions.Peek().At + _window - now).Ticks));
                }
            }

        wait:
            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1), time, cancellationToken).ConfigureAwait(false);
        }
    }
}
