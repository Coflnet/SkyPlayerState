using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class SkyblockEventsTests
{
    private static readonly MayorSnapshot Diaz = new("diaz", [new("Shopping Spree", "")]);

    /// <summary>A real time on <paramref name="dayOfYear"/> of some SkyBlock year that is not in real-world December.</summary>
    private static DateTime At(int dayOfYear, int minutesIntoDay = 5)
    {
        for (var year = 1500; ; year++)
        {
            var time = SkyblockCalendar.Epoch + SkyblockCalendar.YearLength * year
                + SkyblockCalendar.DayLength * dayOfYear + TimeSpan.FromMinutes(minutesIntoDay);
            if (time.Month != 12)
                return time;
        }
    }

    [Test]
    public void Calendar_CountsDaysFromTheSkyblockEpoch()
    {
        SkyblockCalendar.DayOfYear(SkyblockCalendar.Epoch).Should().Be(0);
        SkyblockCalendar.DayOfYear(At(245)).Should().Be(245);
        SkyblockCalendar.DayOfYear(SkyblockCalendar.Epoch - TimeSpan.FromMinutes(1)).Should().Be(SkyblockCalendar.DaysPerYear - 1);
        SkyblockCalendar.Day(SkyblockCalendar.Autumn, 26).Should().Be(7 * 31 + 25);
    }

    [Test]
    public void Calendar_NextStartOf_IsLaterThisYearOrNextYear()
    {
        var now = At(10);
        SkyblockCalendar.NextStartOf(20, now).Should().Be(now - TimeSpan.FromMinutes(5) + SkyblockCalendar.DayLength * 10);
        SkyblockCalendar.NextStartOf(5, now).Should().Be(now - TimeSpan.FromMinutes(5) - SkyblockCalendar.DayLength * 5 + SkyblockCalendar.YearLength);
    }

    [TestCase(SkyblockCalendar.Autumn, 26)]
    [TestCase(SkyblockCalendar.Autumn, 30)]
    [TestCase(SkyblockCalendar.LateAutumn, 3)]
    public void SpookyFestival_IsAvailableWhileTheFearMongererIsInTheHub(int month, int day)
    {
        var availability = SkyblockEvents.SpookyFestival.Check(At(SkyblockCalendar.Day(month, day)), Diaz);

        availability.Should().Be(TaskAvailability.Always);
    }

    [Test]
    public void SpookyFestival_OutsideTheWindow_IsUnavailableWithTheNextStart()
    {
        var now = At(SkyblockCalendar.Day(SkyblockCalendar.LateAutumn, 4));

        var availability = SkyblockEvents.SpookyFestival.Check(now, Diaz);

        availability.Available.Should().BeFalse();
        availability.Note.Should().Contain("Spooky Festival").And.NotContain("Possible again", "a relative time goes stale in web caches");
        availability.NextAvailableAt.Should().Be(SkyblockCalendar.NextStartOf(SkyblockCalendar.Day(SkyblockCalendar.Autumn, 26), now));
        SkyblockCalendar.DayOfYear(availability.NextAvailableAt!.Value).Should().Be(SkyblockCalendar.Day(SkyblockCalendar.Autumn, 26));
    }

    [Test]
    public void SpookyFestival_StaysListedWhileFoxyMaySchedule_AnExtraOne()
    {
        var now = At(10);
        var spookyFoxy = new MayorSnapshot("foxy", [new("Extra Event", "Schedules an extra §6Spooky Festival §7event during the year.")]);
        var miningFoxy = new MayorSnapshot("foxy", [new("Extra Event", "Schedules an extra §6Mining Fiesta §7event during the year.")]);

        SkyblockEvents.SpookyFestival.Check(now, spookyFoxy).Should().Match<TaskAvailability>(a => a.Available && a.Note.Contains("Foxy"));
        SkyblockEvents.SpookyFestival.Check(now, MayorSnapshot.FromName("foxy")).Available.Should().BeTrue("Foxy without perk data may have the extra event");
        SkyblockEvents.SpookyFestival.Check(now, miningFoxy).Available.Should().BeFalse();
    }

    [Test]
    public void JerrysWorkshop_IsOpenAllOfLateWinter_AndInRealDecember()
    {
        SkyblockEvents.JerrysWorkshop.Check(At(SkyblockCalendar.Day(SkyblockCalendar.LateWinter, 1)), null).Should().Be(TaskAvailability.Always);
        SkyblockEvents.JerrysWorkshop.Check(At(SkyblockCalendar.Day(SkyblockCalendar.LateWinter, 31)), null).Should().Be(TaskAvailability.Always);
        SkyblockEvents.JerrysWorkshop.Check(new DateTime(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc), null).Should().Be(TaskAvailability.Always);
        SkyblockEvents.JerrysWorkshop.Check(At(SkyblockCalendar.Day(SkyblockCalendar.LateWinter, 1) - 1), null).Available.Should().BeFalse();
    }

    [Test]
    public void JerrysWorkshop_NextOpening_IsRealDecemberWhenThatComesFirst()
    {
        // 2026-11-27: real December starts in 4 days, sooner than the next Late Winter
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        if (SkyblockEvents.JerrysWorkshop.IsRunningAt(now))
            Assert.Inconclusive("Late Winter happens to run at the chosen time");

        var availability = SkyblockEvents.JerrysWorkshop.Check(now, null);

        var nextLateWinter = SkyblockCalendar.NextStartOf(SkyblockCalendar.Day(SkyblockCalendar.LateWinter, 1), now);
        var december = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        availability.Available.Should().BeFalse();
        availability.NextAvailableAt.Should().Be(nextLateWinter < december ? nextLateWinter : december);
        availability.Note.Should().Contain("Jerry's Workshop");
    }

    [Test]
    public void MythologicalRitual_NeedsDianaOrHerPerk()
    {
        var now = At(10);
        SkyblockEvents.MythologicalRitual.Check(now, MayorSnapshot.FromName("diana")).Should().Be(TaskAvailability.Always);
        SkyblockEvents.MythologicalRitual.Check(now, new MayorSnapshot("someone", [new(MythologicalRitualEvent.PerkName, "")]))
            .Should().Be(TaskAvailability.Always);

        var diaz = SkyblockEvents.MythologicalRitual.Check(now, Diaz);
        diaz.Available.Should().BeFalse();
        diaz.Note.Should().Contain("Diaz").And.Contain("Mythological Ritual");
        diaz.NextAvailableAt.Should().BeNull("the next Diana term depends on an election");
    }

    [Test]
    public void MythologicalRitual_StaysAvailable_WhenTheMayorIsUnknownOrJerry()
    {
        var unknown = SkyblockEvents.MythologicalRitual.Check(At(10), null);
        var jerry = SkyblockEvents.MythologicalRitual.Check(At(10), MayorSnapshot.FromName("jerry"));

        unknown.Available.Should().BeTrue();
        unknown.Note.Should().Contain("could not be checked");
        jerry.Available.Should().BeTrue();
        jerry.Note.Should().Contain("Perkpocalypse");
    }

    [Test]
    public void EventBoundTasks_DeclareTheirEvent()
    {
        var events = new TaskRegistry().PublicMethodTasks
            .Where(t => t.RequiredEvent != null)
            .ToDictionary(TaskEstimator.GetTaskName, t => t.RequiredEvent);

        events.Should().BeEquivalentTo(new Dictionary<string, SkyblockEvent>
        {
            ["Diana"] = SkyblockEvents.MythologicalRitual,
            ["Spooky Fishing"] = SkyblockEvents.SpookyFestival,
            ["Winter Fishing"] = SkyblockEvents.JerrysWorkshop,
            ["Glacial Cave"] = SkyblockEvents.JerrysWorkshop,
        });
    }

    [Test]
    public void EveryTaskWithALocationOnJerrysWorkshop_DeclaresAnEvent()
    {
        // any location on the seasonal island counts, also for tasks that are not registered (yet)
        var onJerry = AllMethodTasks().Where(t => t.LocationsForTest.Any(l => SkyblockZones.IslandOfLocation(l) == "Jerry")).ToList();

        onJerry.Select(t => t.GetType().Name).Should().Contain(["WinterFishingTask", "GlacialCaveTask", "FestivalFishingTask", "FestivalFishingHuntingTask"]);
        onJerry.Should().OnlyContain(t => t.RequiredEvent != null);
    }

    private static IEnumerable<MethodTask> AllMethodTasks() => typeof(MethodTask).Assembly.GetTypes()
        .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(MethodTask)) && t.GetConstructor(Type.EmptyTypes) != null)
        .Select(t => (MethodTask)Activator.CreateInstance(t));

    [Test]
    public void FishingFestival_RunsTheFirstThreeDaysOfAMonth_WithMarinasPerk()
    {
        var marina = new MayorSnapshot("marina", [new(FishingFestivalEvent.PerkName, "")]);
        var month = SkyblockCalendar.Day(SkyblockCalendar.Autumn, 1);

        SkyblockEvents.FishingFestival.Check(At(month), marina).Should().Be(TaskAvailability.Always);
        SkyblockEvents.FishingFestival.Check(At(month + 2), marina).Should().Be(TaskAvailability.Always);
        var after = SkyblockEvents.FishingFestival.Check(At(month + 3), marina);
        after.Available.Should().BeFalse();
        after.NextAvailableAt.Should().Be(SkyblockCalendar.NextStartOf(SkyblockCalendar.Day(SkyblockCalendar.LateAutumn, 1), At(month + 3)));
    }

    [Test]
    public void FishingFestival_NextMonthWrapsIntoTheNextYear()
    {
        var marina = new MayorSnapshot("marina", [new(FishingFestivalEvent.PerkName, "")]);
        var now = At(SkyblockCalendar.Day(SkyblockCalendar.LateWinter, 10));

        var next = SkyblockEvents.FishingFestival.Check(now, marina).NextAvailableAt;

        SkyblockCalendar.DayOfYear(next!.Value).Should().Be(0);
        next.Should().BeAfter(now);
    }

    [Test]
    public void FishingFestival_FailsOpenOnWhatTheMayorDataCannotShow()
    {
        var festivalDay = At(SkyblockCalendar.Day(SkyblockCalendar.Autumn, 2));
        var otherDay = At(SkyblockCalendar.Day(SkyblockCalendar.Autumn, 10));
        var fishingFoxy = new MayorSnapshot("foxy", [new("Extra Event", "Schedules an extra §bFishing Festival §7event during the year.")]);

        // a Marina minister may grant it, which the mayor data does not show
        SkyblockEvents.FishingFestival.Check(festivalDay, Diaz).Should().Match<TaskAvailability>(a => a.Available && a.Note.Contains("minister"));
        SkyblockEvents.FishingFestival.Check(otherDay, null).Should().Match<TaskAvailability>(a => a.Available && a.Note.Contains("could not be checked"));
        SkyblockEvents.FishingFestival.Check(otherDay, fishingFoxy).Should().Match<TaskAvailability>(a => a.Available && a.Note.Contains("Foxy"));
        var diaz = SkyblockEvents.FishingFestival.Check(otherDay, Diaz);
        diaz.Available.Should().BeFalse();
        diaz.NextAvailableAt.Should().BeNull("the next month only has a festival if a Marina minister grants it");
    }

    [Test]
    public async Task CofLTaskPath_UsesTheMayorsPerks()
    {
        // Foxy's actual extra event decides, not just the name: a Mining Fiesta Foxy can't give a Spooky Festival
        var miningFoxy = new MayorSnapshot("foxy", [new("Extra Event", "Schedules an extra §6Mining Fiesta §7event during the year.")]);
        var parameters = Params(At(10));
        parameters.CurrentMayor = "foxy";
        parameters.Mayor = miningFoxy;

        var result = await new SpookyFishingTask().Execute(parameters);

        result.IsAccessible.Should().BeFalse();
    }

    [Test]
    public void EveryPublicTaskOnJerrysWorkshop_NeedsTheIslandOpen()
    {
        // a new task placed on the seasonal island must not show up in the public list all year round
        var onJerry = new TaskRegistry().PublicMethodTasks.Where(t => t.IslandForTest == "Jerry").ToList();

        onJerry.Should().NotBeEmpty();
        onJerry.Should().OnlyContain(t => t.RequiredEvent == SkyblockEvents.JerrysWorkshop);
    }

    [Test]
    public async Task SpookyFishing_OutsideTheFestival_IsInaccessibleWithTheNextStart()
    {
        var now = At(10);

        var result = await new SpookyFishingTask().Execute(Params(now));

        result.IsAccessible.Should().BeFalse();
        result.InaccessibleReason.Should().Contain("Spooky Festival");
        result.NextAvailableAt.Should().Be(SkyblockCalendar.NextStartOf(SkyblockCalendar.Day(SkyblockCalendar.Autumn, 26), now));
    }

    [Test]
    public async Task SpookyFishing_DuringTheFestival_IsAccessible()
    {
        var result = await new SpookyFishingTask().Execute(Params(At(SkyblockCalendar.Day(SkyblockCalendar.Autumn, 29))));

        result.IsAccessible.Should().BeTrue();
        result.NextAvailableAt.Should().BeNull();
    }

    [Test]
    public void Estimate_CarriesTheAvailability()
    {
        var now = At(10);
        var diana = TaskEstimator.WithAvailability(new TaskEstimate(), new DianaTask(), now, Diaz);
        var plain = TaskEstimator.WithAvailability(new TaskEstimate(), new WaterFishingTask(), now, Diaz);

        diana.RequiredEvent.Should().Be("Mythological Ritual");
        diana.Available.Should().BeFalse();
        diana.AvailabilityNote.Should().Contain("Diaz");
        plain.RequiredEvent.Should().BeNull();
        plain.Available.Should().BeTrue();
        plain.AvailabilityNote.Should().BeNull();
        plain.NextAvailableAt.Should().BeNull();
    }

    [Test]
    public async Task CommunityEstimates_FlagDianaUnavailable_WhileAnotherMayorRules()
    {
        var mayorApi = new Mock<Mayor.Client.Api.IMayorApi>();
        mayorApi.Setup(a => a.MayorCurrentGetAsync(It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new Mayor.Client.Model.CoflnetSkyMayorModelsModelCandidate
            {
                Key = "diaz", Name = "Diaz", Perks = [new() { Name = "Shopping Spree", Description = "Cheaper stuff" }]
            });
        var mayors = new CurrentMayorService(mayorApi.Object, NullLogger<CurrentMayorService>.Instance);
        var registry = new TaskRegistry();

        var estimates = await Estimator(registry, mayors).GetCommunityEstimates([]);

        var diana = estimates.Single(e => e.TaskName == "Diana");
        diana.Available.Should().BeFalse();
        diana.RequiredEvent.Should().Be("Mythological Ritual");
        diana.AvailabilityNote.Should().Contain("Diaz");
        estimates.Where(e => e.RequiredEvent == null).Should().OnlyContain(e => e.Available && e.AvailabilityNote == null);
    }

    [Test]
    public async Task CommunityEstimates_KeepDianaAvailable_WhenTheMayorServiceFails()
    {
        var mayorApi = new Mock<Mayor.Client.Api.IMayorApi>();
        mayorApi.Setup(a => a.MayorCurrentGetAsync(It.IsAny<int>(), It.IsAny<System.Threading.CancellationToken>()))
            .ThrowsAsync(new Exception("mayor service down"));
        var mayors = new CurrentMayorService(mayorApi.Object, NullLogger<CurrentMayorService>.Instance);

        var estimates = await Estimator(new TaskRegistry(), mayors).GetCommunityEstimates([]);

        var diana = estimates.Single(e => e.TaskName == "Diana");
        diana.Available.Should().BeTrue();
        diana.AvailabilityNote.Should().Contain("could not be checked");
    }

    private static TaskEstimator Estimator(TaskRegistry registry, CurrentMayorService mayors)
    {
        var session = new Mock<global::Cassandra.ISession>().Object;
        return new TaskEstimator(registry,
            new TaskAggregateService(session, NullLogger<TaskAggregateService>.Instance),
            new StatScoreService(null, NullLogger<StatScoreService>.Instance),
            new TaskActivityService(new Mock<StackExchange.Redis.IConnectionMultiplexer>().Object, registry,
                NullLogger<TaskActivityService>.Instance),
            new CoinValueRegistry(session, NullLogger<CoinValueRegistry>.Instance),
            NullLogger<TaskEstimator>.Instance, mayors);
    }

    private static TaskParams Params(DateTime now) => new()
    {
        TestTime = now, ExtractedInfo = new ExtractedInfo(),
        Formatter = new SimpleTaskFormatProvider(), Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
        LocationProfit = [], CleanPrices = [], BazaarPrices = [], Names = []
    };
}
