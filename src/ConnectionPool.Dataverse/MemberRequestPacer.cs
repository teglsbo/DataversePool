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
    private int? _limit;
    private TimeSpan _window = TimeSpan.FromMinutes(5);

    public (int? Limit, TimeSpan Window) Current
    {
        get { lock (_gate) { return (_limit, _window); } }
    }

    public void Configure(int? maxRequestsPerWindow, TimeSpan? window)
    {
        lock (_gate)
        {
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
                if (_limit is not { } limit)
                {
                    return;
                }

                var now = time.GetUtcNow();
                while (_starts.Count > 0 && now - _starts.Peek() >= _window)
                {
                    _starts.Dequeue();
                }

                if (_starts.Count < limit)
                {
                    _starts.Enqueue(now);
                    return;
                }

                delay = _starts.Peek() + _window - now;
            }

            await Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1), time, cancellationToken).ConfigureAwait(false);
        }
    }
}
