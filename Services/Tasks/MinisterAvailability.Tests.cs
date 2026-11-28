using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Mayor.Client.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Regression: event availability only looked at the mayor's perks, but the minister (the election's runner-up) grants one
/// of their perks for the same term. A minister with an event perk the tasks gate on must make the event available, and a
/// known minister without it must not keep a Fishing Festival "maybe" open.
/// </summary>
public class MinisterAvailabilityTests
{
    private static readonly MayorPerk[] DiazPerks = [new("Shopping Spree", "")];

    private static MayorSnapshot DiazWith(string minister, string perk)
        => new("diaz", DiazPerks, new MinisterSnapshot(minister, new MayorPerk(perk, "")));

    private static readonly MayorSnapshot DiazAlone = new("diaz", DiazPerks);

    private static DateTime At(int dayOfYear)
        => SkyblockCalendar.Epoch + SkyblockCalendar.YearLength * 1500 + SkyblockCalendar.DayLength * dayOfYear + TimeSpan.FromMinutes(5);

    private static readonly DateTime FestivalDay = At(SkyblockCalendar.Day(SkyblockCalendar.Autumn, 2));
    private static readonly DateTime OtherDay = At(SkyblockCalendar.Day(SkyblockCalendar.Autumn, 10));

    // ── Mythological Ritual ──

    [Test]
    public void MythologicalRitual_GrantedByTheMinister_IsAvailable()
    {
        var mayor = DiazWith("diana", MythologicalRitualEvent.PerkName);

        SkyblockEvents.MythologicalRitual.Check(OtherDay, mayor).Should().Be(TaskAvailability.Always);
        new DianaTask().AvailabilityAt(OtherDay, mayor).Available.Should().BeTrue();
    }

    [Test]
    public void MythologicalRitual_NeitherMayorNorMinister_IsUnavailable()
    {
        // the real 2026-11-28 term: Diaz with minister Diana granting Huntress' Intuition, not the ritual
        var availability = SkyblockEvents.MythologicalRitual.Check(OtherDay, DiazWith("diana", "Huntress' Intuition"));

        availability.Available.Should().BeFalse();
        availability.Note.Should().Contain("Diaz");
    }

    [Test]
    public void MythologicalRitual_WithoutMinisterData_IsUnchanged()
    {
        var availability = SkyblockEvents.MythologicalRitual.Check(OtherDay, DiazAlone);

        availability.Available.Should().BeFalse();
        availability.Note.Should().Be("Needs Mayor Diana's Mythological Ritual perk (current mayor: Diaz). Possible again once Diana wins an election.");
    }

    // ── Fishing Festival ──

    [Test]
    public void FishingFestival_GrantedByTheMinister_IsAvailableOnFestivalDays_AndKnowsTheNextOne()
    {
        var mayor = DiazWith("marina", FishingFestivalEvent.PerkName);

        SkyblockEvents.FishingFestival.Check(FestivalDay, mayor).Should().Be(TaskAvailability.Always);
        var later = SkyblockEvents.FishingFestival.Check(OtherDay, mayor);
        later.Available.Should().BeFalse();
        later.NextAvailableAt.Should().NotBeNull("the minister's perk runs a festival every month of the term");
    }

    [Test]
    public void FishingFestival_KnownMinisterWithoutThePerk_IsUnavailableOnFestivalDays()
    {
        var availability = SkyblockEvents.FishingFestival.Check(FestivalDay, DiazWith("cole", "Mining Fiesta"));

        availability.Available.Should().BeFalse();
        availability.NextAvailableAt.Should().BeNull();
    }

    [Test]
    public void FishingFestival_WithoutMinisterData_StaysOpenWithTheNote()
    {
        var availability = SkyblockEvents.FishingFestival.Check(FestivalDay, DiazAlone);

        availability.Available.Should().BeTrue();
        availability.Note.Should().Contain("not checked");
    }

    // ── Foxy as minister ──

    /// <summary>Review regression: Foxy's Extra Event as the minister's perk was ignored, a festival day turned unavailable.</summary>
    [Test]
    public void FishingFestival_FoxyMinisterWithAnExtraFishingFestival_StaysAvailable()
    {
        var mayor = new MayorSnapshot("diaz", DiazPerks, new MinisterSnapshot("foxy",
            new MayorPerk(MayorSnapshot.ExtraEventPerk, "Schedules an extra §bFishing Festival §7event during the year.")));

        SkyblockEvents.FishingFestival.Check(FestivalDay, mayor).Should().Match<TaskAvailability>(a => a.Available && a.Note.Contains("Foxy"));
        SkyblockEvents.FishingFestival.Check(OtherDay, mayor).Should().Match<TaskAvailability>(a => a.Available && a.Note.Contains("Foxy"));
    }

    [Test]
    public void SpookyFestival_FoxyMinisterWithAnExtraSpookyFestival_MayRunOutsideItsDays()
    {
        var mayor = new MayorSnapshot("diaz", DiazPerks, new MinisterSnapshot("foxy",
            new MayorPerk(MayorSnapshot.ExtraEventPerk, "Schedules an extra §6Spooky Festival §7event during the year.")));
        var notSpooky = At(SkyblockCalendar.Day(SkyblockCalendar.LateWinter, 10));

        SkyblockEvents.SpookyFestival.Check(notSpooky, mayor).Should().Match<TaskAvailability>(a => a.Available && a.Note.Contains("Foxy"));
        SkyblockEvents.SpookyFestival.Check(notSpooky, DiazWith("foxy", "Sweet Benevolence")).Available.Should().BeFalse();
    }

    // ── CurrentMayorService loads the minister ──

    private class TestClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 28, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private Mock<Mayor.Client.Api.IMayorApi> mayorApi;
    private Mock<Mayor.Client.Api.IElectionPeriodsApi> electionApi;

    [SetUp]
    public void Setup()
    {
        mayorApi = new();
        mayorApi.Setup(a => a.MayorCurrentGetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CoflnetSkyMayorModelsModelCandidate { Key = "economist", Name = "Diaz", Perks = [new() { Name = "Shopping Spree" }] });
        electionApi = new();
    }

    private readonly TestClock clock = new();

    private CurrentMayorService Service(bool withElectionApi = true)
        => new(mayorApi.Object, NullLogger<CurrentMayorService>.Instance, clock, withElectionApi ? electionApi.Object : null);

    private void FailingPeriods()
        => electionApi.Setup(a => a.ElectionPeriodRangeGetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("down"));

    private void MayorIs(string name)
        => mayorApi.Setup(a => a.MayorCurrentGetAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CoflnetSkyMayorModelsModelCandidate { Key = name.ToLowerInvariant(), Name = name, Perks = [new() { Name = "Perk" }] });

    private void Periods(params CoflnetSkyMayorModelsModelElectionPeriod[] periods)
        => electionApi.Setup(a => a.ElectionPeriodRangeGetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CoflnetSkyMayorModelsModelElectionPeriod>(periods));

    private static CoflnetSkyMayorModelsModelElectionPeriod Period(int year, string winner, string minister, string perk) => new()
    {
        Year = year,
        Winner = new CoflnetSkyMayorModelsModelWinner
        {
            Name = winner,
            Minister = minister == null ? null : new CoflnetSkyMayorModelsMinister { Name = minister, Perk = new() { Name = perk, Minister = true } }
        }
    };

    [Test]
    public async Task LoadsTheMinisterOfTheCurrentTerm()
    {
        Periods(Period(526, "Aatrox", "Diaz", "Stock Exchange"), Period(527, "Diaz", "Diana", MythologicalRitualEvent.PerkName));

        var mayor = await Service().GetCurrentMayorSnapshot();

        mayor.Name.Should().Be("diaz");
        mayor.Minister.Name.Should().Be("diana");
        mayor.HasPerk(MythologicalRitualEvent.PerkName).Should().BeTrue();
        mayor.HasPerk("Shopping Spree").Should().BeTrue();
    }

    [Test]
    public async Task IgnoresTheMinister_WhenTheLatestPeriodBelongsToAnotherMayor()
    {
        // the new term is not stored yet: the latest period is the previous mayor's
        Periods(Period(526, "Aatrox", "Diana", MythologicalRitualEvent.PerkName));

        var mayor = await Service().GetCurrentMayorSnapshot();

        mayor.Minister.Should().BeNull();
        mayor.HasPerk(MythologicalRitualEvent.PerkName).Should().BeFalse();
    }

    [Test]
    public async Task MissingMinisterField_LeavesTheMinisterUnknown()
    {
        Periods(Period(527, "Diaz", null, null));

        var mayor = await Service().GetCurrentMayorSnapshot();

        mayor.Name.Should().Be("diaz");
        mayor.Minister.Should().BeNull();
    }

    [Test]
    public async Task MinisterWithoutPerk_IsUnknown()
    {
        Periods(Period(527, "Diaz", "Diana", null));

        (await Service().GetCurrentMayorSnapshot()).Minister.Should().BeNull();
    }

    [Test]
    public async Task ElectionCall_GetsACancellableToken()
    {
        Periods(Period(527, "Diaz", "Diana", MythologicalRitualEvent.PerkName));

        await Service().GetCurrentMayorSnapshot();

        electionApi.Verify(a => a.ElectionPeriodRangeGetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(),
            It.Is<CancellationToken>(t => t.CanBeCanceled)), Times.Once);
    }

    [Test]
    public async Task TransientElectionFailure_KeepsTheMinister_AndRetriesInFiveMinutes()
    {
        Periods(Period(527, "Diaz", "Diana", MythologicalRitualEvent.PerkName));
        var service = Service();
        await service.GetCurrentMayorSnapshot();
        FailingPeriods();
        clock.Now += TimeSpan.FromHours(2);

        await service.GetCurrentMayorSnapshot(); // stale while revalidate: the refresh fails on the minister
        (await service.GetCurrentMayorSnapshot()).Minister.Name.Should().Be("diana", "the mayor is unchanged");

        Periods(Period(527, "Diaz", "Diana", MythologicalRitualEvent.PerkName));
        clock.Now += TimeSpan.FromMinutes(6);
        await service.GetCurrentMayorSnapshot();
        electionApi.Verify(a => a.ElectionPeriodRangeGetAsync(It.IsAny<long>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3), "a failed lookup is retried after 5 minutes, not after an hour");
    }

    [Test]
    public async Task TransientElectionFailure_DropsTheMinister_WhenTheMayorChanged()
    {
        Periods(Period(527, "Diaz", "Diana", MythologicalRitualEvent.PerkName));
        var service = Service();
        await service.GetCurrentMayorSnapshot();
        FailingPeriods();
        MayorIs("Aatrox");
        clock.Now += TimeSpan.FromHours(2);

        await service.GetCurrentMayorSnapshot();
        var mayor = await service.GetCurrentMayorSnapshot();

        mayor.Name.Should().Be("aatrox");
        mayor.Minister.Should().BeNull();
    }

    [Test]
    public async Task FailingElectionPeriods_StillLoadTheMayor()
    {
        FailingPeriods();

        var mayor = await Service().GetCurrentMayorSnapshot();

        mayor.Name.Should().Be("diaz");
        mayor.Minister.Should().BeNull();
    }

    [Test]
    public async Task WithoutElectionApi_TheMinisterIsUnknown()
    {
        var mayor = await Service(withElectionApi: false).GetCurrentMayorSnapshot();

        mayor.Name.Should().Be("diaz");
        mayor.Minister.Should().BeNull();
    }
}
