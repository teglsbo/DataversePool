using ConnectionPool.Core;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Simple round-robin member selection, ignoring load stats. This is the v1 default; see
/// docs/adr/0006 for the rationale and the future throttle-aware extension point.
/// </summary>
public sealed class RoundRobinSlotSelectionStrategy : ISlotSelectionStrategy
{
    private int _cursor = -1;

    public SlotSelection SelectNext(IReadOnlyList<DataverseUserPool> members, IReadOnlyList<PoolStats> memberStats)
    {
        if (members.Count == 0)
        {
            throw new InvalidOperationException("No member pools to select from.");
        }

        var next = Interlocked.Increment(ref _cursor);
        var index = (int)((uint)next % (uint)members.Count);

        // This strategy has no health/throttle awareness at all, so it never reports "all
        // unavailable" - every member always looks equally eligible to it.
        return new SlotSelection(members[index], AllMembersUnavailable: false);
    }
}
