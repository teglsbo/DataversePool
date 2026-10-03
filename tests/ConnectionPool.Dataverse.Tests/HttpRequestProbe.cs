using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;

namespace ConnectionPool.Dataverse.Tests;

internal sealed class HttpRequestProbe : EventListener
{
    private readonly string _host;
    private readonly ConcurrentDictionary<Guid, ConcurrentQueue<long>> _starts = new();
    private int _started;
    private int _completed;
    private int _active;
    private int _peak;
    private int _throttles;
    private int _otherStatuses;
    private int _unmatched;
    private int _duplicateIds;
    private long _first = long.MaxValue;
    private long _last;

    public HttpRequestProbe(string host) => _host = host;

    public int Started => Volatile.Read(ref _started);
    public int Completed => Volatile.Read(ref _completed);
    public int PeakInFlight => Volatile.Read(ref _peak);
    public int Throttles => Volatile.Read(ref _throttles);
    public int OtherStatuses => Volatile.Read(ref _otherStatuses);
    public int UnmatchedEvents => Volatile.Read(ref _unmatched);
    public int DuplicateIds => Volatile.Read(ref _duplicateIds);
    public int Pending => _starts.Values.Sum(queue => queue.Count);
    public int Active => Volatile.Read(ref _active);
    public TimeSpan LaunchSpread => Started > 1
        ? Stopwatch.GetElapsedTime(Interlocked.Read(ref _first), Interlocked.Read(ref _last))
        : TimeSpan.Zero;

    public void Reset()
    {
        if (Pending != 0)
        {
            throw new InvalidOperationException("Cannot reset HTTP probe with requests in flight.");
        }

        _started = _completed = _active = _peak = _throttles = _otherStatuses = _unmatched = _duplicateIds = 0;
        _first = long.MaxValue;
        _last = 0;
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Net.Http")
        {
            EnableEvents(source, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs item)
    {
        if (item.EventSource.Name != "System.Net.Http")
        {
            return;
        }

        if (item.EventName == "RequestStart")
        {
            if (item.Payload is not { Count: >= 2 } payload || payload[1] is not string host ||
                !string.Equals(host, _host, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            // EventSource activity IDs can be shared by concurrent SDK requests.
            var starts = _starts.GetOrAdd(item.ActivityId, static _ => new ConcurrentQueue<long>());
            if (!starts.IsEmpty)
            {
                Interlocked.Increment(ref _duplicateIds);
            }
            starts.Enqueue(now);
            Interlocked.Increment(ref _started);
            InterlockedMin(ref _first, now);
            InterlockedMax(ref _last, now);
            InterlockedMax(ref _peak, Interlocked.Increment(ref _active));
        }
        else if (item.EventName is "RequestStop" or "RequestFailed" &&
                 _starts.TryGetValue(item.ActivityId, out var starts) && starts.TryDequeue(out _))
        {
            Interlocked.Increment(ref _completed);
            Interlocked.Decrement(ref _active);
            if (item.EventName == "RequestStop" && item.Payload?.Count > 0 &&
                item.Payload[0] is int status)
            {
                if (status == (int)HttpStatusCode.TooManyRequests)
                {
                    Interlocked.Increment(ref _throttles);
                }
                else if (status < 200 || status >= 300)
                {
                    Interlocked.Increment(ref _otherStatuses);
                }
            }
            else
            {
                Interlocked.Increment(ref _otherStatuses);
            }
        }
        else if (item.EventName is "RequestStop" or "RequestFailed" && Pending != 0)
        {
            Interlocked.Increment(ref _unmatched);
        }
    }

    private static void InterlockedMin(ref long target, long value)
    {
        long previous;
        do
        {
            previous = Interlocked.Read(ref target);
            if (value >= previous) return;
        } while (Interlocked.CompareExchange(ref target, value, previous) != previous);
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long previous;
        do
        {
            previous = Interlocked.Read(ref target);
            if (value <= previous) return;
        } while (Interlocked.CompareExchange(ref target, value, previous) != previous);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int previous;
        do
        {
            previous = Volatile.Read(ref target);
            if (value <= previous) return;
        } while (Interlocked.CompareExchange(ref target, value, previous) != previous);
    }
}
