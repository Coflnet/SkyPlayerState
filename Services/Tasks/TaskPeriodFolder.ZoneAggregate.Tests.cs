using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>Only periods the estimate itself folded may reach the per zone "best spot" aggregate.</summary>
public class TaskPeriodFolderZoneAggregateTests
{
    private const string Task = "Diana";

    private class FakeAggregates : TaskAggregateService
    {
        public FakeAggregates() : base(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<TaskAggregateService>.Instance) { }
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task) => System.Threading.Tasks.Task.FromResult<TaskPlayerStatRow>(null);
        public override System.Threading.Tasks.Task UpsertPlayerStat(TaskPlayerStatRow row) => System.Threading.Tasks.Task.CompletedTask;
    }

    private static (TaskPeriodFolder folder, FakeAggregates aggregates) Make()
    {
        var aggregates = new FakeAggregates();
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(System.Threading.Tasks.Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        var folder = new TaskPeriodFolder(aggregates,
            new StatScoreService(null, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance);
        return (folder, aggregates);
    }

    private static Period Session(double minutes) => new()
    {
        PlayerUuid = "aadcde4ae0714253b0a562aa13c9900e", Location = "Ruins", DetectedTask = Task,
        StartTime = new DateTime(2026, 10, 7, 12, 0, 0),
        EndTime = new DateTime(2026, 10, 7, 12, 0, 0).AddMinutes(minutes),
        ItemsCollected = new() { ["X"] = 3 },
        ZoneSeconds = new() { ["Ruins"] = minutes * 40, ["Forest"] = minutes * 20 },
        ZoneValue = new() { ["Ruins"] = 2000, ["Forest"] = 1000 }
    };

    private static readonly Dictionary<string, double> Prices = new() { ["X"] = 1000 };

    [Test]
    public async Task FoldedSession_ContributesItsZoneShares()
    {
        var (folder, aggregates) = Make();

        await folder.Fold(Session(30), new StateObject(), Prices);

        aggregates.PendingZoneForTest(Task, "Ruins").Should().Be((1200, 2000, 1));
        aggregates.PendingZoneForTest(Task, "Forest").Should().Be((600, 1000, 1));
    }

    [Test]
    public async Task TooShortSession_ContributesNothing()
    {
        var (folder, aggregates) = Make();

        await folder.Fold(Session(1), new StateObject(), Prices);

        aggregates.PendingZoneForTest(Task, "Ruins").Should().Be((0, 0, 0));
    }

    [Test]
    public async Task LateReward_ContributesNothing()
    {
        var (folder, aggregates) = Make();

        await folder.FoldLateReward(Session(30), new StateObject(), Prices);

        aggregates.PendingZoneForTest(Task, "Ruins").Should().Be((0, 0, 0));
    }
}
