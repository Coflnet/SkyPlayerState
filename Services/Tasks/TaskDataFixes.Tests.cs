using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

public class TaskDataResetsTests
{
    [Test]
    public void IsBeforeReset_OnlyForListedTasks_AndOnlyBeforeTheCutoff()
    {
        var cutoff = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        TaskDataResets.IsBeforeReset("Fossil Excavation", cutoff.AddSeconds(-1)).Should().BeTrue();
        TaskDataResets.IsBeforeReset("Fossil Excavation", cutoff).Should().BeFalse();
        TaskDataResets.IsBeforeReset("fossil excavation", cutoff.AddDays(-3)).Should().BeTrue();
        TaskDataResets.IsBeforeReset("Fig Foraging", cutoff.AddDays(-30)).Should().BeFalse();
        TaskDataResets.IsBeforeReset(null, cutoff.AddDays(-30)).Should().BeFalse();
    }

    [Test]
    public void Diana_IsReset_AndItsFormulaMatchesTheObservedEventRate()
    {
        TaskDataResets.IsBeforeReset("Diana", new DateTime(2026, 10, 24, 0, 0, 0, DateTimeKind.Utc)).Should().BeTrue();
        // event 2026-10-21..24: ~26M/h over 603 session hours; the aggregate showed 143M/h
        var prices = new Dictionary<string, double>
        {
            ["ANCIENT_CLAW"] = 158.9, ["ENCHANTED_ANCIENT_CLAW"] = 30567, ["GRIFFIN_FEATHER"] = 116553, ["ENCHANTED_GOLD"] = 465,
            ["HILT_OF_REVELATIONS"] = 166600, ["ENCHANTMENT_ULTIMATE_CHIMERA_1"] = 21960041, ["MINOS_RELIC"] = 16170000,
            ["CROCHET_TIGER_PLUSHIE"] = 490000, ["WASHED_UP_SOUVENIR"] = 362600, ["DWARF_TURTLE_SHELMET"] = 390040,
            ["ANTIQUE_REMEDIES"] = 411600, ["CRETAN_URN"] = 195020, ["DAEDALUS_STICK"] = 500000, ["SHARD_KING_MINOS"] = 3340
        };
        TaskEstimator.FormulaRate(new DianaTask(), prices).Should().BeInRange(20e6, 32e6);
    }
}

public class TaskCountTruncationTests
{
    [Test]
    public void KeepCostsAndTop_KeepsEveryNegative_AndTheTopPositives()
    {
        var counts = Enumerable.Range(0, 20).ToDictionary(i => $"ITEM_{i}", i => (double)(i + 1));
        counts["DUNGEON_CHEST_COST"] = -5_000_000;
        counts["KISMET_FEATHER"] = -2;

        var kept = TaskAggregateService.KeepCostsAndTop(counts, 8);

        kept.Should().ContainKey("DUNGEON_CHEST_COST").And.ContainKey("KISMET_FEATHER");
        kept.Count.Should().Be(10);
        kept.Keys.Where(k => k.StartsWith("ITEM_")).Should().BeEquivalentTo(Enumerable.Range(12, 8).Select(i => $"ITEM_{i}"));
    }
}

public class TaskEstimatorDropSourceTests
{
    [Test]
    public void DropSource_PersonalCounts_UsePersonalSeconds()
    {
        var personal = new TaskPlayerStatRow { WSeconds = 3600, ItemCounts = new() { { "A", 10 } } };
        var bucket = new BucketAggregate { WSeconds = 36000, ItemCounts = new() { { "B", 1 } } };

        var (counts, seconds) = TaskEstimator.DropSource(true, personal, bucket);
        counts.Should().ContainKey("A");
        seconds.Should().Be(3600);

        var (bCounts, bSeconds) = TaskEstimator.DropSource(false, personal, bucket);
        bCounts.Should().ContainKey("B");
        bSeconds.Should().Be(36000);
    }
}

public class TaskPeriodFolderFossilTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";
    private const string TaskName = "Fossil Excavation";

    private class FakeAggregates : TaskAggregateService
    {
        public TaskPlayerStatRow Stored;
        public FakeAggregates(TaskPlayerStatRow existing) : base(new Moq.Mock<global::Cassandra.ISession>().Object,
            NullLogger<TaskAggregateService>.Instance)
            => Stored = existing;
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task) => Task.FromResult(Stored);
        public override Task UpsertPlayerStat(TaskPlayerStatRow row)
        {
            Stored = row;
            return Task.CompletedTask;
        }
    }

    private static (TaskPeriodFolder folder, FakeAggregates aggregates) Make(TaskPlayerStatRow existing)
    {
        var aggregates = new FakeAggregates(existing);
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        var folder = new TaskPeriodFolder(aggregates,
            new StatScoreService(null, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance);
        return (folder, aggregates);
    }

    private static Period FossilPeriod(Dictionary<string, int> items) => new()
    {
        PlayerUuid = Player, Location = "Fossil Research Center", DetectedTask = TaskName,
        StartTime = new DateTime(2026, 10, 26, 12, 0, 0), EndTime = new DateTime(2026, 10, 26, 12, 5, 0),
        ItemsCollected = items
    };

    private static readonly Dictionary<string, double> Prices = new()
    {
        { "ESSENCE_FOSSIL", 1752 }, { "SUSPICIOUS_SCRAP", 136850 }
    };

    [Test]
    public async Task Fold_NegativeBucketRate_DoesNotFlipSigns()
    {
        var (folder, aggregates) = Make(null);
        var bucket = new BucketAggregate
        {
            WSeconds = 36000, WPeriods = 10,
            ItemCounts = new() { { "ESSENCE_FOSSIL", 30000 }, { "SUSPICIOUS_SCRAP", -1000 } }
        };
        aggregates.SetSnapshotForTest(Enumerable.Range(0, StatScoreService.BucketCount)
            .ToDictionary(b => (TaskName, (byte)b), b => bucket));

        await folder.Fold(FossilPeriod(new() { { "ESSENCE_FOSSIL", 800 }, { "SUSPICIOUS_SCRAP", -74 } }), new StateObject(), Prices);

        aggregates.Stored.ItemCounts["ESSENCE_FOSSIL"].Should().BeApproximately(800, 1e-6);
        aggregates.Stored.ItemCounts["SUSPICIOUS_SCRAP"].Should().BeApproximately(-74, 1e-6);
        var (_, items) = aggregates.PendingForTest(TaskName);
        items["ESSENCE_FOSSIL"].Should().BeApproximately(400, 1e-6, "only the 0.5 ramp weight scales it");
        items["SUSPICIOUS_SCRAP"].Should().BeApproximately(-37, 1e-6);
    }

    [Test]
    public async Task Fold_ExistingStatFromBeforeReset_IsReplaced_NotAddedTo()
    {
        var stale = new TaskPlayerStatRow
        {
            PlayerUuid = Player, TaskName = TaskName, WSeconds = 36000, CumulativeMinutes = 600,
            ItemCounts = new() { { "ESSENCE_FOSSIL", -50000 } }, LastFold = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc)
        };
        var (folder, aggregates) = Make(stale);

        await folder.Fold(FossilPeriod(new() { { "ESSENCE_FOSSIL", 800 } }), new StateObject(), Prices);

        aggregates.Stored.ItemCounts["ESSENCE_FOSSIL"].Should().BeApproximately(800, 1e-6);
        aggregates.Stored.CumulativeMinutes.Should().BeApproximately(5, 1e-6);
        aggregates.Stored.WSeconds.Should().BeApproximately(300, 1e-6);
    }
}
