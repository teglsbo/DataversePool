using ConnectionPool.Core;

namespace ConnectionPool.Dataverse.Tests;

/// <summary>Test double that always reports every member as unavailable, without ever needing a real Dataverse connection to prove it (DataverseGroupPool must short-circuit before calling member.AcquireAsync when FailFast is configured).</summary>
internal sealed class FakeAllUnavailableStrategy : ISlotSelectionStrategy
{
    public SlotSelection SelectNext(IReadOnlyList<DataverseUserPool> members, IReadOnlyList<PoolStats> memberStats)
        => new(members[0], AllMembersUnavailable: true);
}
