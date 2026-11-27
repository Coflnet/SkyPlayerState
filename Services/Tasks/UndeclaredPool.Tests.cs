using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// The undeclared pool: gains the declared drop gate keeps out of a task's rate are stored per tag and player, promoted
/// into the rate and the drops once <see cref="UndeclaredPool.PromotionPlayers"/> distinct players found them (each
/// player capped at the second largest one), and no early return of the fold loses them or the player's ledger.
/// </summary>
[NonParallelizable]
public class UndeclaredPoolTests
{
    private const string Lathering = "Helix Honey Lathering";
    private const string Book = "ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3";
    private const double BookPrice = 67_000_000;
    private const byte Unknown = StatScoreService.UnknownBucket;
    private static readonly TaskRegistry Registry = new();
    private static readonly Dictionary<string, double> Prices = new() { [Book] = BookPrice, ["SHARD_HIVETHIEF"] = 20_000 };
    private static readonly Dictionary<string, double> DungeonPrices = new() { ["HYPERION"] = 900_000_000, ["ESSENCE_WITHER"] = 2000 };

    private static MethodTask Task(string name) => (MethodTask)Registry.GetByName(name);

    [TearDown]
    public void Reset() => TaskRateOptions.FullValueRates = false;

    // ── fold helpers ──

    private class FakeAggregates : TaskAggregateService
    {
        public TaskPlayerStatRow Stored;
        public readonly List<long> Realized = new();
        public FakeAggregates() : base(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<TaskAggregateService>.Instance) { }
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task) => System.Threading.Tasks.Task.FromResult(Stored);
        public override Task UpsertPlayerStat(TaskPlayerStatRow row)
        {
            Stored = row;
            return System.Threading.Tasks.Task.CompletedTask;
        }
        public override Task AddRealized(string playerUuid, string task, DateTime foldTime, long value)
        {
            Realized.Add(value);
            return System.Threading.Tasks.Task.CompletedTask;
        }
    }

    private static TaskPeriodFolder Folder(TaskAggregateService aggregates)
    {
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(System.Threading.Tasks.Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        return new TaskPeriodFolder(aggregates, new StatScoreService(null, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance);
    }

    private static Period Session(string player, string task, string location, Dictionary<string, int> items) => new()
    {
        PlayerUuid = player, Location = location, DetectedTask = task,
        StartTime = new DateTime(2026, 11, 27, 12, 0, 0), EndTime = new DateTime(2026, 11, 27, 12, 30, 0), ItemsCollected = items
    };

    private static Task FoldBook(FakeAggregates aggregates, string player, int shards = 4)
        => Folder(aggregates).Fold(Session(player, Lathering, "Torrhus Canyon", new() { { Book, 1 }, { "SHARD_HIVETHIEF", shards } }),
            new StateObject(), Prices);

    private static Task FoldChest(FakeAggregates aggregates, Dictionary<string, int> items)
        => Folder(aggregates).Fold(Session("p1", "F7", "The Catacombs (F7)", items), new StateObject(), DungeonPrices);

    private static BucketAggregate Merged(TaskBucketAggregateRow row, double weight = 1)
    {
        var agg = new BucketAggregate { TaskName = Lathering, Bucket = Unknown };
        TaskAggregateService.AddRow(agg, row, weight);
        return agg;
    }

    private static BucketAggregate Promoted(BucketAggregate agg)
    {
        UndeclaredPool.PromoteAll(new() { [(Lathering, Unknown)] = agg });
        return agg;
    }

    private static BucketAggregate PoolOf(string tag, params (string Player, double Coins)[] players) => new()
    {
        UndeclaredPlayerCoins = new() { [tag] = players.ToDictionary(p => p.Player, p => p.Coins) }
    };

    /// <summary>A bucket with hours behind it, so the AFK check and the winsor cap apply.</summary>
    private static BucketAggregate Warm(BucketAggregate agg, string commonTag, double count)
    {
        agg.WSeconds = 10 * 3600;
        agg.WPeriods = 20;
        agg.ItemCounts = new() { [commonTag] = count };
        agg.Contributors = Enumerable.Range(0, 5).Select(i => "w" + i).ToHashSet();
        return agg;
    }

    // ── the fold stores the pool ──

    [Test]
    public async Task Fold_TwoPlayers_StoresTheGatedTagPerPlayer_ButKeepsItOutOfTheRate()
    {
        var aggregates = new FakeAggregates();

        await FoldBook(aggregates, "p1");
        await FoldBook(aggregates, "p2");

        var row = aggregates.PendingRowForTest(Lathering, Unknown);
        row.UndeclaredPlayerCoins.Should().BeEquivalentTo(new Dictionary<string, double> { [$"{Book}|p1"] = BookPrice, [$"{Book}|p2"] = BookPrice });
        row.RareCoins.Should().Be(0);
        var agg = Promoted(Merged(row));
        agg.PromotedUndeclared.Should().BeEmpty();
        TaskEstimator.RateParts(Task(Lathering), agg, Prices).ScaledRareCoins.Should().Be(0);
    }

    [Test]
    public void EvidencePlayers_UnknownPlayerIsNoIndependentEvidence()
    {
        var playerCoins = new Dictionary<string, double> { ["p1"] = 2_000_000, ["p2"] = 2_000_000, ["unknown"] = 50_000_000 };

        UndeclaredPool.EvidencePlayers(playerCoins).Should().Be(2);
        UndeclaredPool.PromotionShares(new() { [Book] = playerCoins }).Should().BeEmpty();
    }

    [Test]
    public async Task Fold_SamePlayerThreeTimes_NeverPromotes()
    {
        var aggregates = new FakeAggregates();

        for (var i = 0; i < 3; i++)
            await FoldBook(aggregates, "p1");

        var agg = Promoted(Merged(aggregates.PendingRowForTest(Lathering, Unknown)));
        agg.UndeclaredPlayerCoins[Book].Should().BeEquivalentTo(new Dictionary<string, double> { ["p1"] = 3 * BookPrice });
        agg.PromotedUndeclared.Should().BeEmpty("one player's shopping is no evidence");
    }

    [Test]
    public async Task Fold_PromotedTag_CountsInThePersonalRate()
    {
        var aggregates = new FakeAggregates();
        aggregates.SetSnapshotForTest(new() { [(Lathering, (byte)1)] = PoolOf(Book, ("a", 2e6), ("b", 2e6), ("c", 2e6)) });

        await FoldBook(aggregates, "p1");

        aggregates.Stored.RareCoins.Should().Be(BookPrice);
        aggregates.PendingRowForTest(Lathering, Unknown).UndeclaredPlayerCoins.Should().ContainKey($"{Book}|p1",
            "promotion is decided when read, the pool keeps it");
    }

    [Test]
    public async Task Fold_UnpromotedTag_StaysOutOfThePersonalRate()
    {
        var aggregates = new FakeAggregates();

        await FoldBook(aggregates, "p1");

        aggregates.Stored.RareCoins.Should().Be(0);
    }

    // ── early returns keep the pool and the ledger ──

    [Test]
    public async Task Fold_LeftoverPeriod_IsRecordedInTheLedger_AndItsNetGainGoesToThePool()
    {
        var aggregates = new FakeAggregates();
        var before = FoldMetrics.CountOf("F7", FoldMetrics.UndeclaredLeftover);

        await FoldChest(aggregates, new() { { PseudoItems.DUNGEON_CHEST_COST, -6_000_000 }, { "HYPERION", 1 }, { "ESSENCE_WITHER", 40 } });

        var net = 900_000_000 - 6_000_000 + 40 * 2000;
        aggregates.Realized.Should().Equal(net);
        var row = aggregates.PendingRowForTest("F7", Unknown);
        row.UndeclaredPlayerCoins.Should().BeEquivalentTo(new Dictionary<string, double> { ["HYPERION|p1"] = net },
            "the tag is stored net of the chest cost it came with");
        row.WSeconds.Should().Be(0, "the cost remainder is not folded into the rate");
        row.WPeriods.Should().Be(0);
        row.ItemCounts.Should().BeEmpty();
        aggregates.Stored.Should().BeNull("no personal rate data either");
        FoldMetrics.CountOf("F7", FoldMetrics.UndeclaredLeftover).Should().Be(before + 1);
    }

    /// <summary>
    /// Regression: a promoted gain whose period left only the chest cost fell through to the AFK check, was classed AFK
    /// against the warm bucket and returned before the pool, the rate and the ledger got anything.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task Fold_PromotedGainWithOnlyACost_IsNotAfk_AgainstAWarmBucketOfTheSameKey(bool fullValueRates)
    {
        TaskRateOptions.FullValueRates = fullValueRates;
        var aggregates = new FakeAggregates();
        var warm = Warm(PoolOf("HYPERION", ("a", 9e8), ("b", 9e8), ("c", 9e8)), "ESSENCE_WITHER", 100_000);
        aggregates.SetSnapshotForTest(new() { [("F7", Unknown)] = warm });
        var afkBefore = FoldMetrics.CountOf("F7", FoldMetrics.Afk);

        await FoldChest(aggregates, new() { { PseudoItems.DUNGEON_CHEST_COST, -6_000_000 }, { "HYPERION", 1 } });

        FoldMetrics.CountOf("F7", FoldMetrics.Afk).Should().Be(afkBefore);
        var row = aggregates.PendingRowForTest("F7", Unknown);
        row.WSeconds.Should().BeGreaterThan(0, "the promoted loot explains the paid chest");
        row.ItemCounts.Should().ContainKey(PseudoItems.DUNGEON_CHEST_COST);
        row.UndeclaredPlayerCoins.Should().ContainKey("HYPERION|p1");
        aggregates.Realized.Should().Equal(900_000_000 - 6_000_000);
    }

    [Test]
    public async Task Fold_AfkPeriod_KeepsItsUndeclaredGains_AndTheLedger()
    {
        var aggregates = new FakeAggregates();
        aggregates.SetSnapshotForTest(new() { [(Lathering, Unknown)] = Warm(new BucketAggregate(), "SHARD_HIVETHIEF", 100_000) });
        var afkBefore = FoldMetrics.CountOf(Lathering, FoldMetrics.Afk);

        await FoldBook(aggregates, "p1", shards: 1);

        FoldMetrics.CountOf(Lathering, FoldMetrics.Afk).Should().Be(afkBefore + 1);
        var row = aggregates.PendingRowForTest(Lathering, Unknown);
        row.WSeconds.Should().Be(0);
        row.UndeclaredPlayerCoins.Should().BeEquivalentTo(new Dictionary<string, double> { [$"{Book}|p1"] = BookPrice });
        aggregates.Realized.Should().Equal((long)(BookPrice + 20_000));
    }

    [TestCase(-6e6, 0.94)]
    [TestCase(-2e8, 0)]
    [TestCase(5e6, 1)]
    public void NetOfRemainder_SharesTheCost_ClampedBetweenNothingAndTheGrossGain(double remainder, double share)
    {
        var net = TaskPeriodFolder.NetOfRemainder(new() { ["A"] = (1, 6e7), ["B"] = (1, 4e7) }, remainder, 0.5);

        net["A"].Should().BeApproximately(6e7 * share * 0.5, 1);
        net["B"].Should().BeApproximately(4e7 * share * 0.5, 1);
    }

    // ── promotion in rate and drops ──

    private static BucketAggregate Lathered(params (string Player, double Coins)[] bookPlayers)
    {
        var agg = Warm(PoolOf(Book, bookPlayers), "SHARD_HIVETHIEF", 400);
        agg.TaskName = Lathering;
        agg.RareCoins = 5_000_000;
        agg.RareItemCoins = new() { ["HELIXIS_X"] = 5_000_000 };
        agg.RareContributors = ["p0", "p1", "p2"];
        return agg;
    }

    private static double RarePerHour(BucketAggregate agg)
    {
        var parts = TaskEstimator.RateParts(Task(Lathering), agg, Prices);
        return parts.ScaledRareCoins * parts.RareEvidenceScale / (agg.WSeconds / 3600.0);
    }

    [Test]
    public void TwoPlayers_StayOutOfRateAndDrops()
    {
        var agg = Promoted(Lathered(("p1", 15e6), ("p2", 15e6)));

        RarePerHour(agg).Should().BeApproximately(5_000_000 / 10.0, 1e-6);
        TaskEstimator.BuildRareDrops(Task(Lathering), agg, Prices).Should().NotContain(d => d.ItemTag == Book);
    }

    [Test]
    public void ThirdPlayerInAnotherBucket_PromotesIntoRateAndDrops_AndTheDropsExplainTheRate()
    {
        var bucket1 = Lathered(("p1", 15e6), ("p2", 15e6));
        var bucket2 = PoolOf(Book, ("p3", 3e6));
        UndeclaredPool.PromoteAll(new() { [(Lathering, (byte)1)] = bucket1, [(Lathering, (byte)2)] = bucket2 });

        bucket1.PromotedUndeclared.Should().BeEquivalentTo(new[] { Book });
        bucket2.PromotedUndeclaredItemCoins[Book].Should().Be(3e6);
        RarePerHour(bucket1).Should().BeApproximately((5_000_000 + 30_000_000) / 10.0, 1e-6);
        var rareDrops = TaskEstimator.BuildRareDrops(Task(Lathering), bucket1, Prices);
        var book = rareDrops.Single(d => d.ItemTag == Book);
        book.Kind.Should().Be(TaskDropRate.RareKind);
        book.ContributionPerHour.Should().BeApproximately(30_000_000 / 10.0, 1e-6);
        rareDrops.Sum(d => d.ContributionPerHour).Should().BeApproximately(RarePerHour(bucket1), 1e-6, "the drops explain the rate");
    }

    /// <summary>Regression: after promotion one player's volume counted without limit (26 bought books next to two found ones).</summary>
    [Test]
    public void OneOneTwentySix_CountsAboutThreeBooks()
    {
        var snapshot = new Dictionary<(string, byte), BucketAggregate>
        {
            [(Lathering, (byte)1)] = Lathered(("p1", BookPrice), ("p2", BookPrice)),
            [(Lathering, (byte)2)] = Warm(PoolOf(Book, ("p3", 26 * BookPrice)), "SHARD_HIVETHIEF", 400)
        };
        UndeclaredPool.PromoteAll(snapshot);

        snapshot[(Lathering, 1)].PromotedUndeclaredItemCoins[Book].Should().Be(2 * BookPrice);
        snapshot[(Lathering, 2)].PromotedUndeclaredItemCoins[Book].Should().Be(BookPrice, "the buyer counts as much as the second largest finder");
        var merged = TaskEstimator.MergeBuckets(snapshot, Lathering);
        merged.PromotedUndeclaredCoins.Should().Be(3 * BookPrice);
        merged.UndeclaredItemCoins()[Book].Should().Be(28 * BookPrice, "everything stays stored");
        var rareDrops = TaskEstimator.BuildRareDrops(Task(Lathering), snapshot[(Lathering, 2)], Prices);
        rareDrops.Single(d => d.ItemTag == Book).ContributionPerHour.Should().BeApproximately(BookPrice / 10.0, 1e-6);
    }

    [Test]
    public void Decay_AgesAPlayerOut()
    {
        var row = new TaskBucketAggregateRow
        {
            UndeclaredPlayerCoins = new() { [$"{Book}|p1"] = 1.5e6, [$"{Book}|p2"] = 1.5e6 }
        };
        var old = new TaskBucketAggregateRow { UndeclaredPlayerCoins = new() { [$"{Book}|p3"] = 1.5e6 } };

        var fresh = Merged(row);
        TaskAggregateService.AddRow(fresh, old, 1);
        var aged = Merged(row);
        TaskAggregateService.AddRow(aged, old, Math.Pow(0.5, 7 / 7.0));

        Promoted(fresh).PromotedUndeclared.Should().Contain(Book);
        Promoted(aged).PromotedUndeclared.Should().BeEmpty("p3's week old 1.5M decayed below MinPlayerCoins");
    }

    [Test]
    public void GlobalTier_IsTheSumOfItsBuckets()
    {
        var snapshot = new Dictionary<(string, byte), BucketAggregate>
        {
            [(Lathering, (byte)1)] = Lathered(("p1", 15e6), ("p2", 40e6)), [(Lathering, (byte)2)] = PoolOf(Book, ("p3", 3e6), ("p1", 5e6))
        };
        UndeclaredPool.PromoteAll(snapshot);

        var merged = TaskEstimator.MergeBuckets(snapshot, Lathering);

        merged.UndeclaredPlayerCoins[Book].Should().HaveCount(3);
        merged.EffectiveRareCoins.Should().BeApproximately(snapshot.Values.Sum(b => b.EffectiveRareCoins), 1e-6);
        merged.PromotedUndeclaredCoins.Should().BeApproximately(20e6 + 20e6 + 3e6, 1e-6, "p2 capped at p1's 20M");
    }

    [Test]
    public void PromotedPlayers_CountAsRareEvidence()
    {
        var agg = Lathered(("p7", 2e6), ("p8", 2e6), ("p9", 2e6));
        agg.RareContributors = ["p0"];

        Promoted(agg).EffectiveRareContributors().Should().BeEquivalentTo(new[] { "p0", "p7", "p8", "p9" });
    }

    [Test]
    public void PromotedTag_AlsoRareAndCommon_IsOneDrop()
    {
        var agg = Lathered(("p1", 10e6), ("p2", 10e6), ("p3", 10e6));
        agg.ItemCounts[Book] = 2;
        agg.RareItemCoins[Book] = 4_000_000;
        agg.RareCoins += 4_000_000;
        Promoted(agg);

        var rare = TaskEstimator.BuildRareDrops(Task(Lathering), agg, Prices);
        var drops = TaskEstimator.WithRareDrops(TaskEstimator.BuildDrops(Task(Lathering), agg.ItemCounts, agg.WSeconds, Prices), rare);

        rare.Should().ContainSingle(d => d.ItemTag == Book);
        drops.Select(d => d.ItemTag).Should().OnlyHaveUniqueItems();
        drops.Single(d => d.ItemTag == Book).ContributionPerHour
            .Should().BeApproximately(2 * BookPrice / 10.0 + (4_000_000 + 30_000_000) / 10.0, 1e-6);
    }

    [Test]
    public async Task CommunityEstimate_ListsThePromotedTag()
    {
        var session = new Moq.Mock<global::Cassandra.ISession>().Object;
        var aggregates = new TaskAggregateService(session, NullLogger<TaskAggregateService>.Instance);
        aggregates.SetSnapshotForTest(new() { [(Lathering, (byte)1)] = Lathered(("p1", 10e6), ("p2", 10e6), ("p3", 10e6)) });
        var estimator = new TaskEstimator(Registry, aggregates, new StatScoreService(null, NullLogger<StatScoreService>.Instance),
            new TaskActivityService(new Moq.Mock<StackExchange.Redis.IConnectionMultiplexer>().Object, Registry,
                NullLogger<TaskActivityService>.Instance),
            new CoinValueRegistry(session, NullLogger<CoinValueRegistry>.Instance), NullLogger<TaskEstimator>.Instance);

        var estimate = (await estimator.EstimateCommunity(Prices, default)).Single(e => e.TaskName == Lathering);

        estimate.Drops.Should().Contain(d => d.ItemTag == Book && d.Kind == TaskDropRate.RareKind);
    }

    [Test]
    public void AggregateEndpoint_ShowsThePool()
    {
        var bucket1 = Lathered(("p1", 15e6), ("p2", 15e6), ("tiny", 1));
        bucket1.UndeclaredTrimmedCoins = 7e6;
        var snapshot = new Dictionary<(string task, byte bucket), BucketAggregate>
        {
            [(Lathering, (byte)1)] = bucket1, [(Lathering, (byte)2)] = PoolOf(Book, ("p3", 3e6))
        };
        UndeclaredPool.PromoteAll(snapshot);

        var info = Controllers.TaskController.BuildAggregateInfo(Task(Lathering), snapshot, Prices).First();

        info.UndeclaredItemCoins.Should().BeEquivalentTo(new Dictionary<string, double> { [Book] = 30e6 + 1 });
        info.UndeclaredPlayers.Should().BeEquivalentTo(new Dictionary<string, int> { [Book] = 3 }, "a negligible player is no evidence");
        info.PromotedUndeclaredItemCoins.Should().BeEquivalentTo(new Dictionary<string, double> { [Book] = 30e6 + 1 });
        info.PromotedUndeclaredCoins.Should().Be(30e6 + 1);
        info.UntracedUndeclaredCoins.Should().Be(7e6);
    }

    // ── storage ──

    [Test]
    public void OldRows_WithoutTheColumns_ReadAndMergeFine()
    {
        var legacy = new TaskBucketAggregateRow { WSeconds = 3600, UndeclaredPlayerCoins = null, UndeclaredTrimmedCoins = null };
        var agg = new BucketAggregate();

        TaskAggregateService.AddRow(agg, legacy, 1);
        var row = new TaskBucketAggregateRow { UndeclaredPlayerCoins = new() { [$"{Book}|p1"] = 1 }, ItemRefValues = new() };
        TaskAggregateService.MergeIntoRow(row, legacy, TaskAggregateService.RowItemLimit);
        var fresh = new TaskBucketAggregateRow { UndeclaredPlayerCoins = null };
        TaskAggregateService.MergeIntoRow(fresh, row, TaskAggregateService.RowItemLimit);

        agg.UndeclaredPlayerCoins.Should().BeEmpty();
        agg.UndeclaredTrimmedCoins.Should().Be(0);
        row.UndeclaredPlayerCoins.Should().ContainKey($"{Book}|p1");
        fresh.UndeclaredPlayerCoins.Should().BeEquivalentTo(new Dictionary<string, double> { [$"{Book}|p1"] = 1 });
        fresh.UndeclaredTrimmedCoins.Should().Be(0);
    }

    [Test]
    public void MalformedEntries_AreIgnored()
    {
        var agg = Merged(new TaskBucketAggregateRow
        {
            UndeclaredPlayerCoins = new() { ["nope"] = 1, ["|p1"] = 1, [$"{Book}|"] = 1, [$"{Book}|p2"] = 2 }
        });

        agg.UndeclaredPlayerCoins.Keys.Should().Equal(Book);
        agg.UndeclaredPlayerCoins[Book].Should().BeEquivalentTo(new Dictionary<string, double> { ["p2"] = 2 });
    }

    [Test]
    public void Trim_RanksTagsByPlayersThenValue_AndKeepsWhatItCutsAsTrimmedCoins()
    {
        var coins = new Dictionary<string, double> { ["RICH_ONE_PLAYER|p0"] = 1e12 };
        for (var t = 0; t < UndeclaredPool.TagLimit; t++)
            for (var p = 0; p < 2; p++)
                coins[$"TAG_{t}|p{p}"] = 1e6;
        for (var p = 0; p < 8; p++)
            coins[$"TAG_0|q{p}"] = 2e6 + p;
        var row = new TaskBucketAggregateRow { UndeclaredPlayerCoins = new(coins) };

        UndeclaredPool.Trim(row);

        row.UndeclaredPlayerCoins.Keys.Should().NotContain("RICH_ONE_PLAYER|p0", "a tag found by more players ranks first");
        row.UndeclaredPlayerCoins.Keys.Count(k => k.StartsWith("TAG_0|")).Should().Be(UndeclaredPool.PlayersPerTag);
        row.UndeclaredPlayerCoins.Should().ContainKey("TAG_0|q7", "the players with the most coins are kept");
        (row.UndeclaredPlayerCoins.Values.Sum() + row.UndeclaredTrimmedCoins).Should().BeApproximately(coins.Values.Sum(), 1e-3,
            "nothing vanishes");
        UndeclaredPool.PlayersPerTag.Should().BeGreaterThanOrEqualTo(UndeclaredPool.PromotionPlayers);
    }

    [Test]
    public void TrimmedCoins_AreReadDecayed_ButNotCounted()
    {
        var agg = Lathered();
        TaskAggregateService.AddRow(agg, new TaskBucketAggregateRow { UndeclaredTrimmedCoins = 8e6 }, 0.5);
        Promoted(agg);

        agg.UndeclaredTrimmedCoins.Should().Be(4e6);
        RarePerHour(agg).Should().BeApproximately(5_000_000 / 10.0, 1e-6);
    }

    /// <summary>The driver can not map enums or byte: the stored rows only use types it maps.</summary>
    [Test]
    public void StoredRows_UseOnlyTypesTheDriverMaps()
    {
        var allowed = new HashSet<Type>
        {
            typeof(string), typeof(int), typeof(long), typeof(double), typeof(double?), typeof(DateTime),
            typeof(Dictionary<string, double>), typeof(HashSet<string>), typeof(List<string>)
        };
        foreach (var type in new[] { typeof(TaskBucketAggregateRow), typeof(TaskPlayerStatRow) })
            type.GetProperties().Where(p => !allowed.Contains(p.PropertyType)).Select(p => $"{type.Name}.{p.Name}")
                .Should().BeEmpty();
        typeof(TaskBucketAggregateRow).GetProperty(nameof(TaskBucketAggregateRow.UndeclaredPlayerCoins))!.PropertyType
            .Should().Be(typeof(Dictionary<string, double>));
        typeof(TaskBucketAggregateRow).GetProperty(nameof(TaskBucketAggregateRow.UndeclaredTrimmedCoins))!.PropertyType
            .Should().Be(typeof(double?));
    }
}
