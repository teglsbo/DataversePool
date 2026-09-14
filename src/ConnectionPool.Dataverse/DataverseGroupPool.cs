using ConnectionPool.Core;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace ConnectionPool.Dataverse;

/// <summary>
/// Composes multiple <see cref="DataverseUserPool"/> instances (typically one per Dataverse
/// application/service user) and selects one per acquire via a pluggable
/// <see cref="ISlotSelectionStrategy"/> (round-robin by default). This is how you scale out beyond
/// a single user's Dataverse service-protection limit - see docs/adr/0006.
///
/// Note this coordination is entirely in-process: throttle/circuit state is not shared across
/// multiple instances of your application (e.g. multiple pods) targeting the same member service
/// principals - see the README's "production constraint" section and docs/adr/0009/0010.
/// </summary>
public sealed class DataverseGroupPool : IAsyncDisposable
{
    private readonly IReadOnlyList<DataverseUserPool> _members;
    private readonly ISlotSelectionStrategy _strategy;
    private readonly GroupAllUnavailableBehavior _allUnavailableBehavior;

    public DataverseGroupPool(
        IEnumerable<DataverseUserPool> members,
        ISlotSelectionStrategy? strategy = null,
        GroupAllUnavailableBehavior allUnavailableBehavior = GroupAllUnavailableBehavior.FailOpen)
    {
        _members = members?.ToArray() ?? throw new ArgumentNullException(nameof(members));
        if (_members.Count == 0)
        {
            throw new ArgumentException("At least one member pool is required.", nameof(members));
        }

        _strategy = strategy ?? new HealthAwareRoundRobinSlotSelectionStrategy();
        _allUnavailableBehavior = allUnavailableBehavior;
    }

    public IReadOnlyList<DataverseUserPool> Members => _members;

    /// <exception cref="DataverseGroupUnavailableException">
    /// Every member is circuit-open/throttled and this pool is configured with
    /// <see cref="GroupAllUnavailableBehavior.FailFast"/>. See docs/adr/0010.
    /// </exception>
    public async Task<DataverseGroupLease> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var stats = _members.Select(m => m.GetStats()).ToArray();
        var selection = _strategy.SelectNext(_members, stats);

        if (selection.AllMembersUnavailable && _allUnavailableBehavior == GroupAllUnavailableBehavior.FailFast)
        {
            throw BuildUnavailableException();
        }

        var lease = await selection.Member.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return new DataverseGroupLease(selection.Member, lease);
    }

    private DataverseGroupUnavailableException BuildUnavailableException()
    {
        var names = _members.Select(m => m.Name).ToArray();
        var earliestRetryAt = _members
            .Select(m => m.ThrottledUntil)
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .DefaultIfEmpty()
            .Min();
        var earliest = earliestRetryAt == default ? (DateTimeOffset?)null : earliestRetryAt;

        var message = earliest is { } when
            ? $"All {names.Length} group member(s) are currently circuit-open or throttled. Earliest known retry: {when:O}."
            : $"All {names.Length} group member(s) are currently circuit-open or throttled.";

        return new DataverseGroupUnavailableException(message, names, earliest);
    }

    /// <summary>
    /// Warms up each member pool in turn. Members are warmed sequentially (not just each member's
    /// own connections) to keep the "never clone in parallel" guarantee (docs/adr/0002) global
    /// across the whole group, not just within a single member pool.
    /// </summary>
    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        foreach (var member in _members)
        {
            await member.WarmupAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var member in _members)
        {
            await member.DisposeAsync().ConfigureAwait(false);
        }
    }
}
