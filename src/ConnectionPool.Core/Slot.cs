namespace ConnectionPool.Core;

internal enum SlotState
{
    Idle,
    Leased,
    Unhealthy,
    Recycling,
}

/// <summary>
/// Internal unit of pooled capacity. A slot's <see cref="Resource"/> may be replaced in place during
/// recycling, but the <see cref="Slot"/> object identity (and thus its accounting against
/// the pool's max size) is stable until the slot is retired by recycle failure, shutdown, or a
/// runtime shrink via <see cref="ResourcePool{T}.SetMaxSize"/>.
/// </summary>
internal sealed class Slot<T> where T : notnull
{
    public T? Resource { get; set; }
    public SlotState State { get; set; } = SlotState.Idle;
    public Exception? LastIncidentException { get; set; }
    public DateTimeOffset? LastIncidentAt { get; set; }
    public bool LastIncidentWasLeak { get; set; }
    public DateTimeOffset BecameIdleAt { get; set; } = DateTimeOffset.UtcNow;

    public PoolIncidentInfo? LastIncident =>
        LastIncidentAt is null ? null : new PoolIncidentInfo(LastIncidentException, LastIncidentAt.Value, LastIncidentWasLeak);

    public void ClearIncident()
    {
        LastIncidentException = null;
        LastIncidentAt = null;
        LastIncidentWasLeak = false;
    }
}
