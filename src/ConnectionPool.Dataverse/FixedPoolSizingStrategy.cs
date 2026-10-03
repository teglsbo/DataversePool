namespace ConnectionPool.Dataverse;

/// <summary>
/// Default strategy: every member keeps its configured <see cref="ConnectionPool.Core.PoolOptions.MaxSize"/>
/// forever. Reproduces the behaviour before automatic sizing existed.
/// </summary>
public sealed class FixedPoolSizingStrategy : IPoolSizingStrategy
{
    public int GetInitialSize(DataverseUserPool member, int configuredMaxSize) => configuredMaxSize;
}
