using System;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class ReadOnlyPeriodsTests
{
    private const string Uuid = "0123456789abcdef0123456789abcdef";
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    // ── read-only guarantee ──

    [Test]
    public void BuiltQueries_AreAccepted()
    {
        var plain = ReadOnlyPeriodQueries.HistoryForPlayer(Uuid, null, 100);
        var paged = ReadOnlyPeriodQueries.HistoryForPlayer(Uuid.ToUpperInvariant(), T0, 100);

        plain.Cql.Should().StartWith("SELECT ").And.EndWith("LIMIT 100");
        paged.Cql.Should().Contain("endtime < ?");
        paged.Values[0].Should().Be(Uuid, "normalized to lower case");
        ReadOnlyPeriodQueries.EnsureReadOnly(plain);
        ReadOnlyPeriodQueries.EnsureReadOnly(paged);
    }

    [Test]
    public void Limit_IsBounded()
    {
        ReadOnlyPeriodQueries.HistoryForPlayer(Uuid, null, 1_000_000).Cql.Should().EndWith($"LIMIT {ReadOnlyPeriodQueries.MaxLimit}");
        ReadOnlyPeriodQueries.HistoryForPlayer(Uuid, null, -5).Cql.Should().EndWith("LIMIT 1");
    }

    [TestCase("")]
    [TestCase("abc")]
    [TestCase("0123456789abcdef0123456789abcdef' OR ''='")]
    [TestCase("0123456789abcdef0123456789abcdeg")]
    [TestCase("0123456789abcdef0123456789abcdef; DROP TABLE x")]
    public void InvalidPlayerUuid_CannotBuildAQuery(string uuid)
    {
        var act = () => ReadOnlyPeriodQueries.HistoryForPlayer(uuid, null, 10);

        act.Should().Throw<ArgumentException>();
    }

    [TestCase("DELETE FROM historyperiods2 WHERE playeruuid = ?")]
    [TestCase("INSERT INTO historyperiods2 (playeruuid) VALUES (?)")]
    [TestCase("UPDATE historyperiods2 SET profit = 1 WHERE playeruuid = ?")]
    [TestCase("TRUNCATE historyperiods2")]
    [TestCase("DROP TABLE historyperiods2")]
    [TestCase("ALTER TABLE historyperiods2 ADD x text")]
    [TestCase("select playeruuid from historyperiods2 where playeruuid = ? limit 5")]
    [TestCase("SELECT * FROM historyperiods2 WHERE playeruuid = ? LIMIT 5")]
    [TestCase("SELECT playeruuid FROM system_auth.roles WHERE playeruuid = ? LIMIT 5")]
    [TestCase("SELECT playeruuid FROM historyperiods2 WHERE playeruuid = ? LIMIT 5; DROP TABLE historyperiods2")]
    [TestCase("SELECT playeruuid FROM historyperiods2 WHERE playeruuid = ? LIMIT 5; DELETE FROM historyperiods2 WHERE playeruuid = ?")]
    [TestCase("SELECT playeruuid FROM historyperiods2 WHERE playeruuid = 'x' LIMIT 5")]
    [TestCase("SELECT playeruuid FROM historyperiods2 WHERE playeruuid = ? ALLOW FILTERING LIMIT 5")]
    [TestCase("SELECT playeruuid, password FROM historyperiods2 WHERE playeruuid = ? LIMIT 5")]
    [TestCase("SELECT playeruuid FROM historyperiods2 LIMIT 5")]
    [TestCase("SELECT playeruuid FROM historyperiods2 WHERE playeruuid = ? LIMIT 50000")]
    [TestCase("SELECT playeruuid FROM historyperiods2 WHERE playeruuid = ? LIMIT 0")]
    [TestCase("SELECT playeruuid FROM locationperiods WHERE playeruuid = ? LIMIT 5")]
    [TestCase("")]
    public void AnythingButTheFixedSelects_IsRefused(string cql)
    {
        var act = () => ReadOnlyPeriodQueries.EnsureReadOnly(new ReadOnlyQuery(cql, [Uuid]));

        act.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void ParametersThatDoNotFitTheStatement_AreRefused()
    {
        var good = ReadOnlyPeriodQueries.HistoryForPlayer(Uuid, T0, 10);

        new Action[]
        {
            () => ReadOnlyPeriodQueries.EnsureReadOnly(good with { Values = [Uuid] }),
            () => ReadOnlyPeriodQueries.EnsureReadOnly(good with { Values = [Uuid, "x"] }),
            () => ReadOnlyPeriodQueries.EnsureReadOnly(good with { Values = [Uuid, T0, 1] }),
            () => ReadOnlyPeriodQueries.EnsureReadOnly(good with { Values = ["not-a-uuid", T0] }),
            () => ReadOnlyPeriodQueries.EnsureReadOnly(good with { Values = null! }),
            () => ReadOnlyPeriodQueries.EnsureReadOnly(null!),
        }.Should().AllSatisfy(a => a.Should().Throw<InvalidOperationException>());
    }

    [Test]
    public void TheRunner_RefusesBeforeTouchingTheSession()
    {
        var runner = new ReadOnlyCqlRunner(null!); // a null session proves nothing is executed

        var act = () => runner.ReadPeriods(new ReadOnlyQuery("DELETE FROM historyperiods2 WHERE playeruuid = ?", [Uuid]));

        act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── stays ──

    private static TrackedProfitService.Period P(string location, int startMin, int endMin, string server = "m1", long profit = 100, string? task = null, params (string, int)[] items)
        => new()
        {
            Location = location, Server = server, StartTime = T0.AddMinutes(startMin), EndTime = T0.AddMinutes(endMin), Profit = profit, DetectedTask = task!,
            ItemsCollected = items.ToDictionary(i => i.Item1, i => i.Item2)
        };

    [Test]
    public void ConsecutivePeriodsOfOneLocation_BecomeOneStay()
    {
        var stays = PeriodStays.Merge([P("Lotus", 10, 15, task: "Fishing", items: ("COAL", 5)), P("Lotus", 0, 5, task: "Fishing", items: ("COAL", 3)), P("Lotus", 5, 10, task: "Other", items: ("IRON", 1))]);

        var stay = stays.Should().ContainSingle().Subject;
        stay.PeriodCount.Should().Be(3);
        stay.Start.Should().Be(T0);
        stay.End.Should().Be(T0.AddMinutes(15));
        stay.DurationSeconds.Should().Be(900);
        stay.Profit.Should().Be(300);
        stay.Tasks.Should().BeEquivalentTo("Fishing", "Other");
        stay.ItemsCollected.Should().BeEquivalentTo(new System.Collections.Generic.Dictionary<string, int> { ["COAL"] = 8, ["IRON"] = 1 });
    }

    [Test]
    public void ZoneChangeServerChangeOrALongGap_StartANewStay()
    {
        var stays = PeriodStays.Merge([
            P("Lotus", 0, 5), P("Lotus", 5, 10),
            P("Hub", 10, 12),
            P("Lotus", 12, 15),
            P("Lotus", 15, 20, server: "m2"),
            P("Lotus", 30, 35, server: "m2")]);

        stays.Select(s => (s.Location, s.PeriodCount)).Should().Equal(("Lotus", 2), ("Hub", 1), ("Lotus", 1), ("Lotus", 1), ("Lotus", 1));
    }

    [Test]
    public void SmallGapInsideTheTolerance_StillMerges()
    {
        PeriodStays.Merge([P("Lotus", 0, 5), P("Lotus", 6, 10)]).Should().ContainSingle();
    }

    [Test]
    public void NoPeriods_NoStays()
    {
        PeriodStays.Merge([]).Should().BeEmpty();
        PeriodStays.Summarize([]).TotalSeconds.Should().Be(0);
    }

    [Test]
    public void Single_KeepsEveryPeriodSeparate_OldestFirst()
    {
        PeriodStays.Single([P("Lotus", 5, 10), P("Lotus", 0, 5)]).Select(s => s.Start).Should().Equal(T0, T0.AddMinutes(5));
    }

    [Test]
    public void Summary_TotalsAndMediansPerLocation()
    {
        var stays = PeriodStays.Merge([P("Lotus", 0, 10), P("Hub", 10, 12), P("Lotus", 12, 18), P("Lotus", 40, 46), P("Lotus", 46, 50)]);

        var summary = PeriodStays.Summarize(stays);

        summary.Periods.Should().Be(5);
        summary.Stays.Should().Be(4);
        summary.TotalSeconds.Should().Be((10 + 2 + 6 + 10) * 60);
        var lotus = summary.ByLocation.Single(l => l.Location == "Lotus");
        lotus.Stays.Should().Be(3);
        lotus.TotalSeconds.Should().Be(26 * 60);
        lotus.MedianSeconds.Should().Be(10 * 60);
        summary.ByLocation[0].Location.Should().Be("Lotus", "largest first");
    }
}
