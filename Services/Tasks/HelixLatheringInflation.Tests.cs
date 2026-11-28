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
/// Regression: Helix Honey Lathering showed 484M coins/h while its listed drops added up to 0.55M/h (production
/// 2026-11-27, bucket 2: 2.84B of untraced rare coins). The fixtures are the real "Profit summary" fragments that fed it:
/// bazaar bought ultimate books, Behemoth pots (the task's own supply) booked as loot and a dungeon chest claim adopted by
/// the next lathering fragment.
/// </summary>
public class HelixLatheringInflationTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";
    private static readonly HelixHoneyLatheringTask Lathering = new();

    private static readonly Dictionary<string, double> Prices = new()
    {
        ["ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3"] = 67_000_006, ["ENCHANTMENT_ULTIMATE_HABANERO_TACTICS_4"] = 105_000_000,
        ["STUFFED_CHILI_PEPPER"] = 21_400_000, ["BEHEMOTH_POT_OF_HONEYCOMB"] = 21_000_000,
        ["FAMILY_SIZED_POT_OF_HONEYCOMB"] = 2_000_000, ["JUMBO_POT_OF_HONEYCOMB"] = 700_000,
        ["SHARD_HIVETHIEF"] = 20_715, ["SHARD_POLLENDART"] = 6_000, ["HONEYCOMB"] = 53,
        ["ENCHANTMENT_REJUVENATE_1"] = 300_000, ["ESSENCE_WITHER"] = 2_000, ["ESSENCE_UNDEAD"] = 450
    };

    private static double PriceOf(string tag)
        => PseudoItems.TryGetCoinValue(tag, out var pseudo) ? pseudo : Prices.GetValueOrDefault(tag);

    /// <summary>What the fold keeps of a period for the task: the owned filter, then the declared drop gate.</summary>
    private static Dictionary<string, int> Kept(Dictionary<string, int> items, MethodTask task)
    {
        var owned = TaskPeriodFolder.FilterOwnedItems(items, task);
        var undeclared = TaskPeriodFolder.UndeclaredExpensive(owned, task, PriceOf);
        return owned.Where(e => !undeclared.ContainsKey(e.Key)).ToDictionary(e => e.Key, e => e.Value);
    }

    // "yishaqu34 at Torrhus Canyon: 1742000163 coins from 197332576x BAZAAR_PURCHASE, 26x ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3"
    [Test]
    public void BazaarBoughtBooks_AreNoLatheringLoot()
        => Kept(new() { { PseudoItems.BAZAAR_PURCHASE, 197_332_576 }, { "ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3", 26 } }, Lathering)
            .Should().BeEmpty("the books are not a drop of the task and the coins spent are purchase evidence");

    // "yishaqu34 at Miria's Hut: 631292184 coins from -2x JUMBO_POT_OF_HONEYCOMB, 16x STUFFED_CHILI_PEPPER,
    // 15796892x BAZAAR_PURCHASE, 3x ENCHANTMENT_ULTIMATE_HABANERO_TACTICS_4"
    [Test]
    public void BoughtPeppersAndBooks_AreExcluded_TheAppliedPotsStayACost()
        => Kept(new()
            {
                { "JUMBO_POT_OF_HONEYCOMB", -2 }, { "STUFFED_CHILI_PEPPER", 16 }, { PseudoItems.BAZAAR_PURCHASE, 15_796_892 },
                { "ENCHANTMENT_ULTIMATE_HABANERO_TACTICS_4", 3 }
            }, Lathering)
            .Should().BeEquivalentTo(new Dictionary<string, int> { { "JUMBO_POT_OF_HONEYCOMB", -2 } });

    // "sxvuu at Spring Path: 168000000 coins from 8x BEHEMOTH_POT_OF_HONEYCOMB",
    // "Core ... 8x BEHEMOTH_POT_OF_HONEYCOMB, -4x FAMILY_SIZED_POT_OF_HONEYCOMB"
    [Test]
    public void BoughtPots_AreNoLoot_AppliedPotsStayACost()
    {
        Kept(new() { { "BEHEMOTH_POT_OF_HONEYCOMB", 8 } }, Lathering).Should().BeEmpty("buying supplies is not a drop");
        Kept(new() { { "BEHEMOTH_POT_OF_HONEYCOMB", 8 }, { "FAMILY_SIZED_POT_OF_HONEYCOMB", -4 }, { "SHARD_HIVETHIEF", 2 } }, Lathering)
            .Should().BeEquivalentTo(new Dictionary<string, int> { { "FAMILY_SIZED_POT_OF_HONEYCOMB", -4 }, { "SHARD_HIVETHIEF", 2 } });
    }

    [Test]
    public void PositiveCostPseudoTag_IsNoLoot()
        => TaskPeriodFolder.FilterOwnedItems(new() { { PseudoItems.DUNGEON_CHEST_COST, 5_000 } }, new F7Task())
            .Should().BeEmpty("a chest cost is negative by construction");

    [Test]
    public void LatheringLoot_StillCounts()
        => Kept(new() { { "SHARD_HIVETHIEF", 3 }, { "HONEYCOMB", 293 }, { "MIRIA_PRIZE", 1 } }, Lathering)
            .Should().HaveCount(3);

    /// <summary>
    /// Regression: the bucket's counts held DUNGEON_CHEST_COST -552,044, so their sum was negative and every expensive item
    /// counted as infrequent (rare pool), however often it dropped.
    /// </summary>
    [Test]
    public void IsInfrequent_IgnoresCosts()
    {
        var bucket = new BucketAggregate
        {
            WPeriods = 24,
            ItemCounts = new() { ["SHARD_POLLENDART"] = 67, ["MIRIA_PRIZE"] = 20, [PseudoItems.DUNGEON_CHEST_COST] = -552_044 }
        };

        TaskPeriodFolder.IsInfrequent("MIRIA_PRIZE", bucket).Should().BeFalse("it is 23% of the bucket's gains");
        TaskPeriodFolder.IsInfrequent("HELIXIS", bucket).Should().BeTrue();
    }

    [Test]
    public void SplitRareAndCommon_ItemizesTheRarePool()
    {
        var split = TaskPeriodFolder.SplitRareAndCommon(new() { { "MIRIA_PRIZE", 2 }, { "SHARD_HIVETHIEF", 3 } },
            tag => tag == "MIRIA_PRIZE" ? 1_800_000 : 20_000, _ => true);

        split.RareItemCoins.Should().BeEquivalentTo(new Dictionary<string, double> { ["MIRIA_PRIZE"] = 3_600_000 });
        split.RareCoins.Should().Be(3_600_000);
        split.ItemValue.Should().Be(3_660_000);
    }

    // ── fold end to end ──

    private class FakeAggregates : TaskAggregateService
    {
        public TaskPlayerStatRow Stored;
        public FakeAggregates() : base(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<TaskAggregateService>.Instance) { }
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task) => Task.FromResult(Stored);
        public override Task UpsertPlayerStat(TaskPlayerStatRow row)
        {
            Stored = row;
            return Task.CompletedTask;
        }
    }

    private static TaskPeriodFolder Folder(TaskAggregateService aggregates)
    {
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        return new TaskPeriodFolder(aggregates, new StatScoreService(null, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance);
    }

    private static Period Session(Dictionary<string, int> items) => new()
    {
        PlayerUuid = Player, Location = "Torrhus Canyon", DetectedTask = "Helix Honey Lathering",
        StartTime = new DateTime(2026, 11, 26, 21, 0, 0), EndTime = new DateTime(2026, 11, 26, 21, 30, 0), ItemsCollected = items
    };

    [Test]
    public async Task Fold_TheBooksSession_AddsNoRareCoins()
    {
        var aggregates = new FakeAggregates();
        var prices = Prices.ToDictionary(e => e.Key, e => e.Value);

        await Folder(aggregates).Fold(Session(new()
        {
            { PseudoItems.BAZAAR_PURCHASE, 197_332_576 }, { "ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3", 26 }, { "SHARD_HIVETHIEF", 4 }
        }), new StateObject(), prices);

        var row = aggregates.PendingRowForTest("Helix Honey Lathering", StatScoreService.UnknownBucket);
        row.RareCoins.Should().Be(0);
        row.RefItemValue.Should().BeLessThan(1_000_000, "only the shards count");
        row.ItemCounts.Should().ContainKey("SHARD_HIVETHIEF").And.NotContainKey("ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3");
    }

    [Test]
    public async Task Fold_ADeclaredRareDrop_IsStoredPerTag()
    {
        var aggregates = new FakeAggregates();

        await Folder(aggregates).Fold(Session(new() { { "MIRIA_PRIZE", 2 }, { "SHARD_HIVETHIEF", 4 } }), new StateObject(),
            new() { ["MIRIA_PRIZE"] = 1_800_000, ["SHARD_HIVETHIEF"] = 20_000 });

        var row = aggregates.PendingRowForTest("Helix Honey Lathering", StatScoreService.UnknownBucket);
        row.RareCoins.Should().BeGreaterThan(0);
        row.RareItemCoins.Should().ContainKey("MIRIA_PRIZE");
        row.RareItemCoins.Values.Sum().Should().BeApproximately(row.RareCoins, 1e-6, "every rare coin is traceable to a tag");
    }

    // ── sessions ──

    private static readonly TaskSessionService Sessions = new(new TaskClassifier(new TaskRegistry()));

    private static Period Fragment(string location, Dictionary<string, int> items, DateTime end, double seconds = 60) => new()
    {
        PlayerUuid = Player, Location = location, Server = "test", StartTime = end.AddSeconds(-seconds), EndTime = end,
        ItemsCollected = items
    };

    /// <summary>The real fragment sequence of 2026-11-26 21:00: the shopping fragment was adopted by the next shard fragment.</summary>
    [Test]
    public void ShoppingFragment_IsNotAdoptedByTheNextLatheringFragment()
    {
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 11, 26, 21, 2, 15, DateTimeKind.Utc);
        var books = new Dictionary<string, int> { { PseudoItems.BAZAAR_PURCHASE, 197_332_576 }, { "ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3", 26 } };

        // the books fragment started when the previous one ended, 171 seconds earlier
        Sessions.Accumulate(info, Player, Fragment("Torrhus Canyon", books, t0, 171), null, Prices, t0).Should().BeNull();
        var flushed = Sessions.Accumulate(info, Player, Fragment("Torrhus Canyon", new() { { "SHARD_HIVETHIEF", 4 } }, t0.AddSeconds(101), 101),
            null, Prices, t0.AddSeconds(101));
        var third = t0.AddSeconds(967);
        Sessions.Accumulate(info, Player, Fragment("Torrhus Canyon",
            new() { { "SHARD_HIVETHIEF", 3 }, { "HONEYCOMB", 293 } }, third, 866), null, Prices, third);

        flushed.Should().NotBeNull("the shopping trip is flushed on its own");
        flushed.DetectedTask.Should().BeNull();
        flushed.ItemsCollected.Should().ContainKey("ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3");
        info.CurrentSession.Items.Should().NotContainKey("ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3");
        info.CurrentSession.DetectedTask.Should().Be("Helix Honey Lathering");
    }

    /// <summary>
    /// A dungeon chest claimed at Croesus (short, so unclassified) followed by lathering on Torrhus: the claim joined the
    /// lathering session, which explains DUNGEON_CHEST_COST, REJUVENATE and essence in the Helix bucket.
    /// </summary>
    [Test]
    public void DungeonClaimFromAnotherIsland_IsNotAdoptedByLathering()
    {
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 11, 26, 20, 0, 0, DateTimeKind.Utc);
        var claim = new Dictionary<string, int>
        {
            { PseudoItems.DUNGEON_CHEST_COST, -1_000_000 }, { "ENCHANTMENT_REJUVENATE_1", 1 }, { "ESSENCE_WITHER", 40 }, { "ESSENCE_UNDEAD", 54 }
        };

        Sessions.Accumulate(info, Player, Fragment("Dungeon Hub", claim, t0), null, Prices, t0).Should().BeNull();
        var flushed = Sessions.Accumulate(info, Player, Fragment("Torrhus Canyon", new() { { "SHARD_POLLENDART", 3 }, { "HONEYCOMB", 36 } },
            t0.AddSeconds(200), 200), null, Prices, t0.AddSeconds(200));

        flushed.Should().NotBeNull();
        flushed.ItemsCollected.Should().ContainKey(PseudoItems.DUNGEON_CHEST_COST);
        info.CurrentSession.Items.Should().NotContainKey(PseudoItems.DUNGEON_CHEST_COST).And.NotContainKey("ESSENCE_WITHER");
    }

    /// <summary>A slow start of the same activity on the same island is still adopted (the first fragments were too short).</summary>
    [Test]
    public void ShortFirstFragmentOfTheSameActivity_IsStillAdopted()
    {
        var info = new ExtractedInfo();
        var t0 = new DateTime(2026, 11, 26, 20, 0, 0, DateTimeKind.Utc);

        Sessions.Accumulate(info, Player, Fragment("Torrhus Canyon", new() { { "SHARD_POLLENDART", 2 } }, t0), null, Prices, t0);
        Sessions.Accumulate(info, Player, Fragment("Torrhus Canyon", new() { { "SHARD_POLLENDART", 3 } }, t0.AddSeconds(200), 200),
            null, Prices, t0.AddSeconds(200)).Should().BeNull();

        info.CurrentSession.Items["SHARD_POLLENDART"].Should().Be(5);
        info.CurrentSession.DetectedTask.Should().Be("Helix Honey Lathering");
    }

    [Test]
    public void HelixData_BeforeTheFix_IsReset()
    {
        var cutoff = new DateTime(2026, 11, 29, 0, 0, 0, DateTimeKind.Utc);
        TaskDataResets.IsBeforeReset("Helix Honey Lathering", cutoff.AddSeconds(-1)).Should().BeTrue();
        TaskDataResets.IsBeforeReset("Helix Honey Lathering", cutoff).Should().BeFalse();
    }
    /// <summary>
    /// Regression (2026-11-28): the task had no FormulaDrops, so while the data reset hides its old rows the public estimate was
    /// 0 coins/h. The seed (sibling Fig Honey Lathering rates) gives a few million an hour at bazaar prices of 2026-11-28.
    /// </summary>
    [Test]
    public void FormulaSeed_GivesAnEstimateWhileTheResetHidesTheOldRows()
    {
        var task = (MethodTask)new TaskRegistry().GetByName("Helix Honey Lathering");
        var prices = new Dictionary<string, double>
        {
            ["SHARD_POLLENDART"] = 3_005, ["SHARD_PARCHED"] = 5_998, ["SHARD_HIVETHIEF"] = 109,
            ["ENCHANTED_HONEYCOMB"] = 15_260, ["ENCHANTED_HELIX_LOG"] = 3_200, ["HELIXIS"] = 509_357
        };

        TaskEstimator.HasPricedFormulaDrop(task, prices).Should().BeTrue();
        TaskEstimator.FormulaRate(task, prices).Should().BeInRange(3_000_000, 10_000_000);
    }
}
