namespace ConnectionPool.Dataverse;

/// <summary>
/// Stable metric identity for the operation-level instruments described by
/// <see cref="DataverseOperationMetrics"/>. Selects no exporter and holds no callbacks - collection
/// is controlled entirely by whoever subscribes to <see cref="DataverseOperationMetrics.MeterName"/>.
/// See docs/adr/0024.
/// </summary>
public sealed class DataverseOperationMetricsOptions
{
    /// <summary>Value of <see cref="PoolName"/> when none is configured.</summary>
    public const string DefaultPoolName = "default";

    /// <summary>
    /// Value of the <see cref="DataverseOperationMetrics.PoolNameTag"/> tag. Must be a stable
    /// configuration value (e.g. <c>"orders-import"</c>) - never derived from request data, tenant
    /// URLs, or identities, which would explode metric cardinality or leak sensitive values.
    /// </summary>
    public string PoolName { get; init; } = DefaultPoolName;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(PoolName))
        {
            throw new ArgumentException("PoolName must not be empty.", nameof(PoolName));
        }
    }
}
