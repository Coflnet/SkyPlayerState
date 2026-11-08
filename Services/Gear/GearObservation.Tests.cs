using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Period = Coflnet.Sky.PlayerState.Services.TrackedProfitService.Period;

namespace Coflnet.Sky.PlayerState.Services.Gear;

public class GearObservationTests
{
    private const string Player = "aadcde4ae0714253b0a562aa13c9900e";
    private static readonly DateTime Start = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc); // a Thursday
    private static readonly DateTime End = Start.AddMinutes(30);

    private static GearSnapshot FreshGear()
    {
        var gear = GearSessionTrackerTests.Gear(GearNormalizer.Normalize(GearTestItems.Hoe())!,
            pet: new GearPet { Type = "ELEPHANT", Tier = "LEGENDARY", Level = 100 });
        gear.ArmorAt = gear.ToolAt = gear.PetAt = Start.AddMinutes(1);
        gear.EquipmentAt = Start.AddHours(-2);
        gear.Slots[GearSnapshot.Necklace] = new GearPiece { Tag = "LOTUS_NECKLACE", Enchants = new() { ["green_thumb"] = 5 } };
        return gear;
    }

    private static GearFoldInput Input(GearSnapshot? gear = null, double weight = 1, double seconds = 1800, bool changed = false,
        Dictionary<string, double>? sums = null, Dictionary<string, int>? samples = null, ExtractedInfo? info = null) => new(
        new Period
        {
            PlayerUuid = Player, Location = "Plot - 3", StartTime = Start, EndTime = End, DetectedTask = "Wheat Farming",
            Gear = gear ?? FreshGear(), GearChanged = changed, StatSums = sums ?? new(), StatSamples = samples ?? new(),
            LastStatSampleAt = End.AddMinutes(-2)
        },
        "Wheat Farming", 2, seconds, weight, 1_000_000, 250_000, 1_250_000, info);

    private static GearObservationRecord Build(GearFoldInput input)
        => GearObservationBuilder.Build(input, "key", End).Record!;

    [Test]
    public void PlayerKey_IsTheDashlessUuid()
    {
        GearPlayerKey.Of(Guid.Parse("aadcde4a-e071-4253-b0a5-62aa13c9900e")).Should().Be(Player);
    }

    [TestCase(0.4, 1800, false, "low_weight")]
    [TestCase(1.0, 599, false, "too_short")]
    [TestCase(1.0, 1800, true, "gear_changed")]
    public void LowQualitySessions_AreNotObserved(double weight, double seconds, bool changed, string reason)
    {
        var (record, skip) = GearObservationBuilder.Build(Input(weight: weight, seconds: seconds, changed: changed), "key", End);

        record.Should().BeNull();
        skip.Should().Be(reason);
    }

    [Test]
    public void SessionWithoutGear_IsNotObserved()
    {
        var input = Input() with { Period = new Period { PlayerUuid = Player, StartTime = Start, EndTime = End } };

        GearObservationBuilder.Build(input, "key", End).SkipReason.Should().Be("no_gear");
    }

    [Test]
    public void Observation_HoldsTheFoldNumbers_AndTheCategoryFilteredGear()
    {
        var record = Build(Input());
        var o = record.Observation;

        o.TaskName.Should().Be("Wheat Farming");
        o.Category.Should().Be(GearModifierTable.Farming);
        o.WeekStart.Should().Be(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc));
        o.PlayerKey.Should().Be("key");
        o.Seconds.Should().Be(1800);
        o.StatBucket.Should().Be(2);
        o.CommonCoins.Should().Be(1_000_000);
        o.RareCoins.Should().Be(250_000);
        o.ToolEvidence.Should().Be(GearToolEvidence.Optimal);
        o.Flags.Should().BeEmpty();
        o.GearFingerprint.Should().MatchRegex("^[0-9a-f]{16}$");
        var tool = record.Gear.Slots[GearSnapshot.Tool];
        tool.Enchants.Should().ContainKey("turbo_wheat").And.NotContainKey("turbo_carrot");
        tool.Counters["farmed_cultivating"].Should().Be(1_000_000, "counters are stored floored to buckets");
        record.Gear.Pet!.Level.Should().Be(100);
        tool.Stats["Farming Fortune"].Should().Be(60, "lore stats are stored with the gear");
        record.GearJson.Should().NotContain(GearTestItems.HoeUuid).And.NotContain(Player);
    }

    [Test]
    public void StaleParts_AreDroppedAndFlagged()
    {
        var gear = FreshGear();
        gear.ArmorAt = Start.AddHours(-1);
        gear.EquipmentAt = Start.AddDays(-9);
        gear.PetAt = Start.AddHours(-7);

        var record = Build(Input(gear));

        record.Observation.Flags.Should().BeEquivalentTo("armor_stale", "equipment_stale", "pet_stale");
        record.Gear.Slots.Keys.Should().Equal(GearSnapshot.Tool);
        record.Gear.Pet.Should().BeNull();
    }

    [Test]
    public void AmbiguousTool_IsLeftOut()
    {
        var gear = FreshGear();
        gear.ToolEvidence = GearToolEvidence.Ambiguous;

        var record = Build(Input(gear));

        record.Observation.Flags.Should().Contain("tool_ambiguous");
        record.Gear.Slots.Should().NotContainKey(GearSnapshot.Tool);
    }

    [Test]
    public void SameGear_SameFingerprint_EvenWithGrownCounters()
    {
        var other = FreshGear();
        other.Slots[GearSnapshot.Tool] = GearNormalizer.Normalize(GearTestItems.Hoe(1_400_000))!;

        Build(Input(other)).Observation.GearFingerprint.Should().Be(Build(Input()).Observation.GearFingerprint);
    }

    [Test]
    public void Stats_TabMeans_WhenNoBaseExists()
    {
        var record = Build(Input(sums: new() { ["Farming Fortune"] = 3000 }, samples: new() { ["Farming Fortune"] = 2 }));

        record.Observation.StatsSource.Should().Be(GearObservationBuilder.StatsTab);
        record.Observation.Stats["Farming Fortune"].Should().Be(1500);
        record.Observation.StatsAgeSeconds.Should().Be(120);
    }

    [Test]
    public void Stats_BasePlusTheSessionsGear_WinOverTabSamples()
    {
        // FreshGear: helmet Farming Fortune +30, hoe +60, necklace and pet without lore stats
        var info = BaseInfo();

        var record = Build(Input(info: info, sums: new() { ["Farming Fortune"] = 3000 }, samples: new() { ["Farming Fortune"] = 2 }));

        record.Observation.StatsSource.Should().Be(GearObservationBuilder.StatsBaseGear);
        record.Observation.Stats.Should().BeEquivalentTo(new Dictionary<string, double> { ["Farming Fortune"] = 1090, ["Wheat Fortune"] = 50 },
            "other crops' fortune and mining stats are not farming wheat stats");
        record.Observation.StatsAgeSeconds.Should().Be(3 * 86_400);
    }

    private static readonly Guid Profile = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static ExtractedInfo BaseInfo(Dictionary<string, double>? held = null, GearPet? pet = null) => new()
    {
        CurrentProfileId = Profile,
        BaseStats = new BaseStatsReading
        {
            Stats = new() { ["Farming Fortune"] = 1000, ["Wheat Fortune"] = 50, ["Melon Slice Fortune"] = 7, ["Mining Speed"] = 20 },
            At = End.AddDays(-3), ProfileId = Profile, HeldStats = held ?? new(), Pet = pet
        }
    };

    /// <summary>Regression: the narrowed gear dropped stale armor from the stats while the base had it subtracted.</summary>
    [Test]
    public void Stats_CountTheFullSessionGear_EvenWhenStalePartsAreLeftOutOfTheKey()
    {
        var gear = FreshGear();
        gear.ArmorAt = Start.AddHours(-1);

        var record = Build(Input(gear, info: BaseInfo()));

        record.Observation.Flags.Should().Contain("armor_stale");
        record.Gear.Slots.Should().NotContainKey(GearSnapshot.Helmet);
        record.Observation.Stats["Farming Fortune"].Should().Be(1090, "the stale helmet's +30 still counts");
    }

    [Test]
    public void Stats_WithoutASessionTool_LeaveOutWhatTheHeldItemWasSubtractedFrom()
    {
        var gear = FreshGear();
        gear.Slots.Remove(GearSnapshot.Tool);
        gear.ToolEvidence = GearToolEvidence.Ambiguous;

        var record = Build(Input(gear, info: BaseInfo(held: new() { ["Farming Fortune"] = 60 })));

        record.Observation.Stats.Should().NotContainKey("Farming Fortune");
        record.Observation.Stats["Wheat Fortune"].Should().Be(50);
        record.Observation.Flags.Should().Contain("stats_without_tool");
    }

    [Test]
    public void Stats_AnotherProfilesBase_IsNotUsed()
    {
        var info = BaseInfo();
        info.CurrentProfileId = Guid.NewGuid();

        Build(Input(info: info)).Observation.StatsSource.Should().Be(GearObservationBuilder.StatsNone);
    }

    [Test]
    public void Stats_APetWithoutLore_BorrowsTheBasePetsStats()
    {
        // FreshGear's Elephant has no lore stats (tab or Pets list); the base read showed it with +50 Farming Fortune
        var pet = new GearPet { Type = "ELEPHANT", Tier = "LEGENDARY", Stats = new() { ["Farming Fortune"] = 50 } };

        Build(Input(info: BaseInfo(pet: pet))).Observation.Stats["Farming Fortune"].Should().Be(1140);
    }

    [Test]
    public void Stats_None_WithoutBaseOrSamples()
    {
        var record = Build(Input());

        record.Observation.StatsSource.Should().Be(GearObservationBuilder.StatsNone);
        record.Observation.Stats.Should().BeEmpty();
    }

    private class FakeStore : IGearObservationStore
    {
        public List<(GearObservation Observation, string? Json)> Written = new();
        public List<string> Deleted = new();
        public Task Write(GearObservation observation, string? gearJson, int ttlSeconds)
        {
            Written.Add((observation, gearJson));
            return Task.CompletedTask;
        }
        public Task DeletePlayer(string playerKey)
        {
            Deleted.Add(playerKey);
            return Task.CompletedTask;
        }
    }

    private static (GearObservationService Service, FakeStore Store) Service()
    {
        var store = new FakeStore();
        var service = new GearObservationService(store, new GearObservationOptions(), NullLogger<GearObservationService>.Instance) { Now = () => End };
        return (service, store);
    }

    private static async Task Drain(GearObservationService service)
    {
        for (var i = 0; i < 200 && service.Pending > 0; i++)
            await Task.Delay(10);
    }

    [Test]
    public async Task Service_WritesWithoutAnyConfig_KeyedByTheUuid_AndTheGearJsonOncePerFingerprint()
    {
        var (service, store) = Service();

        service.Record(Input());
        await Drain(service);
        service.Record(Input());
        await Drain(service);

        store.Written.Should().HaveCount(2);
        store.Written[0].Observation.PlayerKey.Should().Be(Player);
        store.Written[0].Json.Should().NotBeNull();
        store.Written[1].Json.Should().BeNull("an unchanged fingerprint is not rewritten within a day");
    }

    [Test]
    public void Options_NeedNoConfig()
    {
        var options = GearObservationOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        options.TtlDays.Should().Be(90);
        options.TtlSeconds.Should().Be(90 * 86_400);
        CassandraGearObservationStore.GearTtl(options.TtlSeconds).Should().Be(91 * 86_400, "gear rows outlive their observations by a day");
    }

    [Test]
    public async Task Service_DeletePlayer_DeletesByUuid_TwiceForWritesInFlight()
    {
        var (service, store) = Service();

        await service.DeletePlayer(Guid.Parse(Player));

        store.Deleted.Should().HaveCount(2).And.OnlyContain(k => k == Player);
    }

    [Test]
    public async Task Service_AfterDeletion_QueuedAndNewObservationsOfThePlayerAreDiscarded()
    {
        var (service, store) = Service();
        var queued = Build(Input());
        queued.Observation.PlayerKey = Player;
        await service.DeletePlayer(Guid.Parse(Player));

        await service.Write(queued);
        service.Record(Input());
        await Drain(service);

        store.Written.Should().BeEmpty();
    }

    [Test]
    public void WeekStart_IsMondayUtc()
    {
        GearObservationBuilder.WeekStart(new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc)).Day.Should().Be(2);
        GearObservationBuilder.WeekStart(new DateTime(2026, 11, 8, 23, 59, 0, DateTimeKind.Utc)).Day.Should().Be(2);
        GearObservationBuilder.WeekStart(new DateTime(2026, 11, 9, 0, 0, 1, DateTimeKind.Utc)).Day.Should().Be(9);
    }

    [Test]
    public void BucketCounter_LogScale_AndToolLevelTens()
    {
        GearFingerprint.BucketCounter("farmed_cultivating", 0).Should().Be(0);
        GearFingerprint.BucketCounter("farmed_cultivating", 7).Should().Be(5);
        GearFingerprint.BucketCounter("farmed_cultivating", 37).Should().Be(20);
        GearFingerprint.BucketCounter("farmed_cultivating", 1_234_567).Should().Be(1_000_000);
        GearFingerprint.BucketCounter("expertise_kills", 5_500).Should().Be(5_000);
        GearFingerprint.BucketCounter("levelable_lvl", 47).Should().Be(40);
    }
}
