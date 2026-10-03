using ConnectionPool.Dataverse;
using Xunit;

namespace ConnectionPool.Dataverse.Tests;

public class DataversePoolIdentityTests
{
    private static readonly Guid UserA = Guid.NewGuid();
    private static readonly Guid UserB = Guid.NewGuid();
    private static readonly Guid Org1 = Guid.NewGuid();
    private static readonly Guid Org2 = Guid.NewGuid();

    [Fact]
    public void DistinctUsers_NoDuplicates()
    {
        var result = DataversePool.FindDuplicateIdentities([("a", UserA, Org1), ("b", UserB, Org1)]);
        Assert.Empty(result);
    }

    [Fact]
    public void SameUserSameOrg_ReportedAsGroup()
    {
        var result = DataversePool.FindDuplicateIdentities([("a", UserA, Org1), ("b", UserA, Org1), ("c", UserB, Org1)]);
        var group = Assert.Single(result);
        Assert.Equal(["a", "b"], group);
    }

    [Fact]
    public void SameUserDifferentOrg_IsNotDuplicate()
    {
        var result = DataversePool.FindDuplicateIdentities([("a", UserA, Org1), ("b", UserA, Org2)]);
        Assert.Empty(result);
    }
}
