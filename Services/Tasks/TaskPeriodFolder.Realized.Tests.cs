using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Controllers;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>The realized value ledger only records Folded and LateReward outcomes.</summary>
public class TaskPeriodFolderRealizedTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";
    private const string Task = "Diana";

    private class FakeAggregates : TaskAggregateService
    {
        public List<(string player, string task, long value)> Realized = new();
        public bool FailRealized;
        public FakeAggregates() : base(new Moq.Mock<global::Cassandra.ISession>().Object,
            NullLogger<TaskAggregateService>.Instance) { }
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task) => System.Threading.Tasks.Task.FromResult<TaskPlayerStatRow>(null);
        public override System.Threading.Tasks.Task UpsertPlayerStat(TaskPlayerStatRow row) => System.Threading.Tasks.Task.CompletedTask;
        public override System.Threading.Tasks.Task AddRealized(string playerUuid, string task, DateTime foldTime, long value)
        {
            if (FailRealized)
                throw new InvalidOperationException("scylla down");
            Realized.Add((playerUuid, task, value));
            return System.Threading.Tasks.Task.CompletedTask;
        }
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

    private static Period Session(double minutes, Dictionary<string, int> items) => new()
    {
        PlayerUuid = Player, Location = "Ruins", DetectedTask = Task,
        StartTime = new DateTime(2026, 10, 7, 12, 0, 0),
        EndTime = new DateTime(2026, 10, 7, 12, 0, 0).AddMinutes(minutes),
        ItemsCollected = items
    };

    private static readonly Dictionary<string, double> Prices = new() { ["X"] = 1000 };

    [Test]
    public async Task Folded_RecordsItemValue()
    {
        var (folder, aggregates) = Make();
        await folder.Fold(Session(10, new() { ["X"] = 3 }), new StateObject(), Prices);
        aggregates.Realized.Should().ContainSingle().Which.Should().Be((Player, Task, 3000L));
    }

    [Test]
    public async Task LateReward_RecordsItemValue()
    {
        var (folder, aggregates) = Make();
        await folder.FoldLateReward(Session(1, new() { ["X"] = 2 }), new StateObject(), Prices);
        aggregates.Realized.Should().ContainSingle().Which.value.Should().Be(2000);
    }

    [Test]
    public async Task TooShort_RecordsNothing()
    {
        var (folder, aggregates) = Make();
        await folder.Fold(Session(1, new() { ["X"] = 3 }), new StateObject(), Prices);
        aggregates.Realized.Should().BeEmpty();
    }

    [Test]
    public async Task NoOwnedItems_RecordsNothing()
    {
        var (folder, aggregates) = Make();
        await folder.Fold(Session(10, new()), new StateObject(), Prices);
        aggregates.Realized.Should().BeEmpty();
    }

    [Test]
    public async Task LedgerFailure_DoesNotCountAsFoldError()
    {
        var (folder, aggregates) = Make();
        aggregates.FailRealized = true;
        var errors = FoldMetrics.CountOf(Task, FoldMetrics.Error);
        await folder.Fold(Session(10, new() { ["X"] = 3 }), new StateObject(), Prices);
        FoldMetrics.CountOf(Task, FoldMetrics.Error).Should().Be(errors);
    }

    [Test]
    public void BuildRealized_SumsTotalAndPerTask()
    {
        var rows = new List<TaskRealizedDailyRow>
        {
            new() { Task = "Diana", Value = 100 }, new() { Task = "Diana", Value = -30 }, new() { Task = "Fishing", Value = 50 }
        };
        var result = TaskController.BuildRealized(rows, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        result.Total.Should().Be(120);
        result.PerTask.Should().BeEquivalentTo(new Dictionary<string, long> { ["Diana"] = 70, ["Fishing"] = 50 });
    }
}
