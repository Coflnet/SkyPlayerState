using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Services.Gear;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>Only the Folded outcome of a regular fold reaches the gear observations.</summary>
public class TaskPeriodFolderGearObservationTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";

    private class FakeAggregates : TaskAggregateService
    {
        public List<long> Realized = new();
        public FakeAggregates() : base(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<TaskAggregateService>.Instance) { }
        public override Task<TaskPlayerStatRow> GetPlayerStat(string playerUuid, string task) => Task.FromResult<TaskPlayerStatRow>(null!);
        public override Task UpsertPlayerStat(TaskPlayerStatRow row) => Task.CompletedTask;
        public override Task AddRealized(string playerUuid, string task, DateTime foldTime, long value)
        {
            Realized.Add(value);
            return Task.CompletedTask;
        }
    }

    private class RecordingSink : IGearObservationSink
    {
        public List<GearFoldInput> Recorded = new();
        public void Record(GearFoldInput input) => Recorded.Add(input);
        public Task DeletePlayer(Guid playerUuid) => Task.CompletedTask;
    }

    private static (TaskPeriodFolder Folder, RecordingSink Sink, FakeAggregates Aggregates) Make()
    {
        var sink = new RecordingSink();
        var aggregates = new FakeAggregates();
        var transactions = new Moq.Mock<ITransactionService>();
        transactions.Setup(t => t.GetTransactions(Moq.It.IsAny<Guid>(), Moq.It.IsAny<TimeSpan>(), Moq.It.IsAny<DateTime>()))
            .Returns(Task.FromResult<IEnumerable<Transaction>>(new List<Transaction>()));
        var folder = new TaskPeriodFolder(aggregates,
            new StatScoreService(null!, NullLogger<StatScoreService>.Instance), new TaskRegistry(),
            new CoinValueRegistry(new Moq.Mock<global::Cassandra.ISession>().Object, NullLogger<CoinValueRegistry>.Instance),
            transactions.Object, NullLogger<TaskPeriodFolder>.Instance, sink);
        return (folder, sink, aggregates);
    }

    private static Period Session(double minutes) => new()
    {
        PlayerUuid = Player, Location = "Ruins", DetectedTask = "Diana",
        StartTime = new DateTime(2026, 10, 7, 12, 0, 0), EndTime = new DateTime(2026, 10, 7, 12, 0, 0).AddMinutes(minutes),
        ItemsCollected = new() { ["X"] = 3 }, Gear = new GearSnapshot()
    };

    private static readonly Dictionary<string, double> Prices = new() { ["X"] = 1000 };

    [Test]
    public async Task Folded_IsRecordedWithTheFoldNumbers()
    {
        var (folder, sink, _) = Make();
        var state = new StateObject();

        await folder.Fold(Session(20), state, Prices);

        var input = sink.Recorded.Should().ContainSingle().Subject;
        input.Task.Should().Be("Diana");
        input.Seconds.Should().Be(1200);
        input.ItemValue.Should().Be(3000);
        input.CommonCoins.Should().Be(3000);
        input.Info.Should().BeSameAs(state.ExtractedInfo);
    }

    [Test]
    public async Task LateReward_IsFoldedButNotRecorded()
    {
        var (folder, sink, aggregates) = Make();

        await folder.FoldLateReward(Session(20), new StateObject(), Prices);

        aggregates.Realized.Should().Equal([3000L], "the late reward path folded the period");
        sink.Recorded.Should().BeEmpty();
    }

    [Test]
    public async Task TooShort_IsNotRecorded()
    {
        var (folder, sink, aggregates) = Make();

        await folder.Fold(Session(1), new StateObject(), Prices);

        aggregates.Realized.Should().BeEmpty("a too short period is dropped before the fold");
        sink.Recorded.Should().BeEmpty();
    }
}
