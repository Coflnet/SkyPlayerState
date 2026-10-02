using AwesomeAssertions;
using Cassandra.Mapping;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class MethodAggregateServiceTests
{
    /// <summary>
    /// 2026-10-02: every pod logged "Failed to record method aggregate: Missing CLUSTERING ORDER for column itemtag"
    /// for its first period, because only the second clustering key had a sort order.
    /// </summary>
    [Test]
    public void EveryClusteringKeyNamesItsSortOrder()
    {
        var keys = ((ITypeDefinition)MethodAggregateService.Mapping).ClusteringKeys;

        keys.Should().HaveCount(2);
        keys.Should().OnlyContain(k => k.Item2 != SortOrder.Unspecified);
    }
}
