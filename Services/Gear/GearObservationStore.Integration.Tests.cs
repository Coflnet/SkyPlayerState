using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Cassandra;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using static Coflnet.Sky.PlayerState.Services.Gear.GearClusterTestData;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The real <see cref="CassandraGearObservationStore"/> against a Scylla node: set <c>GEAR_SCYLLA_IT=host:port</c>
/// (e.g. a throwaway <c>scylladb/scylla</c> container), otherwise the test is skipped. Uses its own keyspace and drops it.
/// </summary>
[Category("Integration")]
public class GearObservationStoreIntegrationTests
{
    private static (string Host, int Port)? Target()
    {
        var value = Environment.GetEnvironmentVariable("GEAR_SCYLLA_IT");
        if (string.IsNullOrEmpty(value))
            return null;
        var parts = value.Split(':');
        return (parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 9042);
    }

    private static GearObservation ToObservation(GearClusterInput input)
    {
        var row = input.Observation;
        return new GearObservation
        {
            TaskName = Wheat,
            WeekStart = GearObservationBuilder.WeekStart(row.FoldedAt),
            FoldedAt = row.FoldedAt,
            ObservationId = TimeUuid.NewId(new DateTimeOffset(row.FoldedAt)).ToGuid(),
            PlayerKey = row.PlayerKey,
            Category = row.Category,
            Seconds = row.Seconds,
            RampWeight = 1,
            CommonCoins = row.CommonCoins,
            RareCoins = row.RareCoins,
            ItemValue = row.CommonCoins + row.RareCoins,
            GearFingerprint = row.GearFingerprint,
            Flags = row.Flags.ToHashSet(),
            Stats = row.Stats.ToDictionary(s => s.Key, s => s.Value),
            StatsSource = row.StatsSource
        };
    }

    [Test]
    public async Task Store_WritesAndPagesBack_AndTheServiceDerivesClusters()
    {
        if (Target() is not var (host, port))
        {
            Assert.Ignore("set GEAR_SCYLLA_IT=host:port to run against Scylla");
            return;
        }
        using var cluster = Cluster.Builder().AddContactPoint(host).WithPort(port).Build();
        var admin = await cluster.ConnectAsync();
        var keyspace = "gear_it_" + Guid.NewGuid().ToString("N")[..10];
        await admin.ExecuteAsync(new SimpleStatement(
            $"CREATE KEYSPACE {keyspace} WITH replication = {{'class': 'SimpleStrategy', 'replication_factor': 1}}"));
        try
        {
            await RoundTrip(await cluster.ConnectAsync(keyspace));
        }
        finally
        {
            await admin.ExecuteAsync(new SimpleStatement($"DROP KEYSPACE IF EXISTS {keyspace}"));
        }
    }

    private static async Task RoundTrip(ISession session)
    {
        var store = new CassandraGearObservationStore(session, new GearObservationOptions(), NullLogger<CassandraGearObservationStore>.Instance)
        {
            ReadPageSize = 3
        };
        var full = TestGear(harvesting: 6);
        var lower = TestGear(harvesting: 5);
        var stats = new System.Collections.Generic.Dictionary<string, double> { ["Farming Fortune"] = 1234.5 };
        var inputs = Enumerable.Range(0, 10)
            .Select(i => Obs(i % 4 + 1, i % 2 == 0 ? full : lower, 1, 1000 + 100 * i, rarePerHour: i, at: Now.AddMinutes(-30 * (i + 1)),
                stats: i == 0 ? stats : null, flags: i == 1 ? ["bait_stale"] : []))
            .Append(Obs(5, full, 2, 9000, at: Now.AddDays(-8)))
            .ToList();
        foreach (var input in inputs)
            await store.Write(ToObservation(input), GearFingerprint.ToJson(input.Gear), 3600);

        var week = GearObservationBuilder.WeekStart(Now);
        var rows = await store.ReadWeek(Wheat, week, 1000, CancellationToken.None);
        rows.Should().HaveCount(10, "four pages of three rows, the previous week's row is another partition");
        rows.Select(r => r.FoldedAt).Should().BeInDescendingOrder();
        (await store.ReadWeek(Wheat, week, 4, CancellationToken.None)).Should().HaveCount(4, "the limit stops the paging");
        var first = rows.Single(r => r.Stats.Count > 0);
        first.Should().BeEquivalentTo(inputs[0].Observation, o => o.Excluding(r => r.FoldedAt));
        first.FoldedAt.Should().BeCloseTo(inputs[0].Observation.FoldedAt, TimeSpan.FromMilliseconds(1));
        rows.Single(r => r.Flags.Count > 0).Flags.Should().BeEquivalentTo(["bait_stale"]);

        var gear = await store.ReadGear([Fingerprint(full), Fingerprint(lower), "0000000000000000"], CancellationToken.None);
        gear.Keys.Should().BeEquivalentTo([Fingerprint(full), Fingerprint(lower)]);
        GearFingerprint.ToJson(gear[Fingerprint(full)]).Should().Be(GearFingerprint.ToJson(full));

        var service = new GearClusterService(store, new Tasks.TaskRegistry(), null!, NullLogger<GearClusterService>.Instance);
        var result = await service.Refresh(Wheat, Now);
        var l2 = result.Clusters.Single(c => c.Level == GearSetupKeys.WithPet);
        l2.Players.Should().Be(5, "players 1-4 this week and player 5 last week");
        result.Clusters.Where(c => c.Level == GearSetupKeys.WithModifiers).Should().HaveCount(2, "harvesting 6 (players 1, 3, 5) and 5 (players 2, 4)");

        await store.DeletePlayer(Player(5));
        (await store.ReadWeek(Wheat, week.AddDays(-7), 1000, CancellationToken.None)).Should().BeEmpty("the deletion found the row by its index");
    }
}
