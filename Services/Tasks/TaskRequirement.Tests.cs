using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class TaskRequirementTests
{
    private static TaskParams Params(PlayerProfileSnapshot snapshot, string mayor = null) => new()
    {
        TestTime = new DateTime(2025, 7, 24, 17, 0, 0),
        ExtractedInfo = new ExtractedInfo(),
        Formatter = new SimpleTaskFormatProvider(),
        Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
        LocationProfit = [],
        CleanPrices = [],
        BazaarPrices = [],
        Names = [],
        CurrentMayor = mayor,
        ProfileSnapshot = snapshot
    };

    /// <summary>A player who has done everything up to M6 at Catacombs 36, as the base for the M7 cases.</summary>
    private static PlayerProfileSnapshot Veteran()
    {
        var snapshot = new PlayerProfileSnapshot { CatacombsLevel = 36, SkyBlockLevel = 300 };
        foreach (var skill in new[] { "combat", "mining", "foraging", "fishing" })
            snapshot.SkillLevels[skill] = 40;
        for (var floor = 0; floor <= 7; floor++)
            snapshot.NormalFloorCompletions[floor] = 10;
        for (var floor = 1; floor <= 6; floor++)
            snapshot.MasterFloorCompletions[floor] = 10;
        snapshot.KuudraCompletions[1] = 5;
        snapshot.KuudraReputation = 20_000;
        return snapshot;
    }

    // ── Snapshot mapping ──

    private const string SampleMember = """
    {
      "player_data": { "experience": { "SKILL_COMBAT": 1222425, "SKILL_MINING": 22425, "SKILL_FORAGING": 1174, "SKILL_FISHING": 67425 } },
      "leveling": { "experience": 31250 },
      "dungeons": { "dungeon_types": {
        "catacombs": { "experience": 17559640, "tier_completions": { "0": 3, "1": 20, "7": 4 } },
        "master_catacombs": { "tier_completions": { "1": 9, "6": 2 } } } },
      "nether_island_player_data": { "kuudra_completed_tiers": { "none": 12, "hot": 3 }, "mages_reputation": 4200, "barbarians_reputation": 900 },
      "slayer": { "slayer_bosses": {
        "zombie": { "xp": 100000, "boss_kills_tier_0": 50, "boss_kills_tier_1": 40, "boss_kills_tier_2": 30, "boss_kills_tier_3": 10 },
        "spider": { "xp": 15, "boss_kills_tier_0": 2, "boss_kills_tier_1": 1 } } },
      "skill_tree": { "experience": { "mining": 12000 } }
    }
    """;

    [Test]
    public void FromMemberJson_MapsLevelsCompletionsKuudraAndSlayers()
    {
        var snapshot = PlayerProfileSnapshot.FromMemberJson(SampleMember);

        snapshot.SkillLevels["combat"].Should().Be(22);
        snapshot.SkillLevels["mining"].Should().Be(12);
        snapshot.SkillLevels["foraging"].Should().Be(4, "1,174 is one XP short of level 5");
        snapshot.SkillLevels["fishing"].Should().Be(15);
        snapshot.SkyBlockLevel.Should().Be(312);
        snapshot.CatacombsLevel.Should().Be(36);
        snapshot.NormalFloorCompletions[7].Should().Be(4);
        snapshot.MasterFloorCompletions[1].Should().Be(9);
        snapshot.MasterFloorCompletions.Should().NotContainKey(7);
        snapshot.KuudraCompletions.Should().BeEquivalentTo(new Dictionary<int, int> { [1] = 12, [2] = 3 });
        snapshot.KuudraReputation.Should().Be(4200, "the higher faction counts");
        snapshot.Slayers["zombie"].HighestTierKilled.Should().Be(4);
        snapshot.Slayers["zombie"].Level.Should().Be(7);
        snapshot.Slayers["spider"].HighestTierKilled.Should().Be(2);
        snapshot.Slayers["wolf"].HighestTierKilled.Should().Be(0, "a boss that was never killed has no counters");
        snapshot.HotmTier.Should().Be(3, "12,000 is exactly the cumulative cost of tier 3");
    }

    [TestCase(1_174, 4)]
    [TestCase(1_175, 5)]
    [TestCase(9_925, 10)]
    [TestCase(22_425, 12)]
    [TestCase(67_425, 15)]
    [TestCase(522_425, 20)]
    [TestCase(1_222_425, 22)]
    [TestCase(3_022_425, 25)]
    [TestCase(99_999_999, 25)]
    public void SkillLevelFromXp_UsesTheCumulativeThresholds(double xp, int level)
    {
        PlayerProfileSnapshot.LevelFromXp(xp, PlayerProfileSnapshot.SkillXpCumulative).Should().Be(level);
    }

    [TestCase(49, 0)]
    [TestCase(50, 1)]
    [TestCase(235, 3)]
    [TestCase(3_045, 9)]
    [TestCase(488_640, 24)]
    [TestCase(17_559_639, 35)]
    [TestCase(17_559_640, 36)]
    public void CatacombsLevelFromXp_UsesTheCumulativeThresholds(double xp, int level)
    {
        PlayerProfileSnapshot.LevelFromXp(xp, PlayerProfileSnapshot.CatacombsXpCumulative).Should().Be(level);
    }

    [Test]
    public void FromMemberJson_MissingSections_AreUnknownOrZeroAsDocumented()
    {
        var snapshot = PlayerProfileSnapshot.FromMemberJson("""{ "player_data": {} }""");

        snapshot.SkillLevels.Should().BeEmpty("without the experience map the skill API is off, so skills are unknown");
        snapshot.SkyBlockLevel.Should().BeNull();
        snapshot.HotmTier.Should().BeNull();
        snapshot.CatacombsLevel.Should().Be(0, "no dungeons section means no dungeon progress");
        snapshot.NormalFloorCompletions.Should().BeEmpty();
        snapshot.KuudraCompletions.Should().BeEmpty();
        snapshot.KuudraReputation.Should().Be(0);
        snapshot.Slayers["blaze"].HighestTierKilled.Should().Be(0);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("""{ "success": false, "cause": "Invalid API key" }""")]
    public void FromMemberJson_NotAMemberObject_IsNull(string json)
    {
        PlayerProfileSnapshot.FromMemberJson(json).Should().BeNull();
    }

    [Test]
    public void FromMemberJson_WrongTypedFields_DoNotThrow()
    {
        var snapshot = PlayerProfileSnapshot.FromMemberJson("""
            { "player_data": { "experience": "x" }, "leveling": { "experience": "y" },
              "dungeons": { "dungeon_types": { "catacombs": { "experience": null, "tier_completions": [] } } },
              "nether_island_player_data": { "kuudra_completed_tiers": 5, "mages_reputation": "many" },
              "slayer": { "slayer_bosses": { "zombie": 3 } } }
            """);

        snapshot.Should().NotBeNull();
        snapshot.SkyBlockLevel.Should().BeNull();
        snapshot.KuudraReputation.Should().Be(0);
    }

    [Test]
    public async Task ProfileSnapshotService_NameInsteadOfUuid_YieldsNull()
    {
        var service = new ProfileSnapshotService(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProfileSnapshotService>.Instance);
        (await service.Get("Ekwav")).Should().BeNull();
    }

    // ── Dungeons ──

    [Test]
    public async Task M7_Catacombs31_IsInaccessible_NamingCatacombs36()
    {
        var snapshot = Veteran();
        snapshot.CatacombsLevel = 31;

        var result = await new M7Task().ExecuteWithRequirements(Params(snapshot));

        result.IsAccessible.Should().BeFalse();
        result.InaccessibleReason.Should().Be("Needs Catacombs 36 (you: 31)");
        result.Breakdown.RequirementsMet.Should().BeFalse();
        result.Breakdown.Requirements.Single(r => r.Label == "Catacombs 36").Met.Should().BeFalse();
    }

    [Test]
    public async Task M7_Catacombs36_ButM6NotCompleted_IsInaccessible()
    {
        var snapshot = Veteran();
        snapshot.MasterFloorCompletions.Remove(6);

        var result = await new M7Task().ExecuteWithRequirements(Params(snapshot));

        result.IsAccessible.Should().BeFalse();
        result.InaccessibleReason.Should().Be("Needs Clear Master Floor 6 (you: 0 clears)");
    }

    [Test]
    public async Task M7_AllRequirementsMet_IsAccessible()
    {
        var result = await new M7Task().ExecuteWithRequirements(Params(Veteran()));

        result.IsAccessible.Should().BeTrue();
        result.InaccessibleReason.Should().BeNull();
        result.Breakdown.RequirementsMet.Should().BeTrue();
        result.Breakdown.Requirements.Should().OnlyContain(r => r.Met == true);
    }

    [Test]
    public async Task M7_WithoutSnapshot_IsAccessible_WithUnknownRequirements()
    {
        var result = await new M7Task().ExecuteWithRequirements(Params(null));

        result.IsAccessible.Should().BeTrue();
        result.Breakdown.RequirementsMet.Should().BeNull();
        result.Breakdown.Requirements.Should().NotBeEmpty().And.OnlyContain(r => r.Met == null);
    }

    [Test]
    public async Task M7_RequirementsListFollowsTheProgression()
    {
        var result = await new M7Task().ExecuteWithRequirements(Params(null));

        result.Breakdown.Requirements.Select(r => r.Label).Should().Equal("Combat 15", "Catacombs 36", "Clear Master Floor 6");
    }

    [Test]
    public async Task Floor1_NeedsOnlyCombat15_SoTheFirstFloorIsNotLockedForNewPlayers()
    {
        var newPlayer = new PlayerProfileSnapshot();
        newPlayer.SkillLevels["combat"] = 15;

        var result = await new F1Task().ExecuteWithRequirements(Params(newPlayer));

        result.IsAccessible.Should().BeTrue();
        result.Breakdown.Requirements.Select(r => r.Label).Should().Equal("Combat 15");
    }

    [Test]
    public async Task MasterOne_NeedsFloor7Cleared()
    {
        var snapshot = Veteran();
        snapshot.CatacombsLevel = 24;
        snapshot.NormalFloorCompletions.Remove(7);

        var result = await new M1Task().ExecuteWithRequirements(Params(snapshot));

        result.InaccessibleReason.Should().Be("Needs Clear Floor 7 (you: 0 clears)");
    }

    [Test]
    public async Task EveryFloorTask_RequiresTheCatacombsLevelItsHowToStates()
    {
        // BaseDungeonTask owns the level table, the floor classes still print their own level - they must agree
        foreach (var task in new ProfitTask[] { new F2Task(), new F3Task(), new F4Task(), new F5Task(), new F6Task(), new F7Task(),
            new M1Task(), new M2Task(), new M3Task(), new M4Task(), new M5Task(), new M6Task(), new M7Task() })
        {
            var result = await task.ExecuteWithRequirements(Params(null));
            var level = result.Breakdown.Requirements.Single(r => r.Kind == RequirementKinds.CatacombsLevel).Target;
            var howTo = (string)task.GetType().GetProperty("HowTo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(task);
            howTo.Should().Contain($"Catacombs level {level}+", task.Name);
        }
    }

    [Test]
    public void M7Kismet_CostsOneFeatherPerRun_AndDoublesTheHandleRate()
    {
        var plain = new M7Task();
        var kismet = new M7KismetTask();

        kismet.FormulaCostsForTest.Single(c => c.ItemTag == "KISMET_FEATHER").RatePerHour.Should().Be(M7Task.RunsPerHour);
        M7Task.RunsPerHour.Should().Be(7);
        var plainHandle = plain.FormulaDropsForTest.Single(d => d.ItemTag == "NECRON_HANDLE").RatePerHour;
        kismet.FormulaDropsForTest.Single(d => d.ItemTag == "NECRON_HANDLE").RatePerHour.Should().Be(2 * plainHandle);
    }

    // ── Kuudra ──

    [Test]
    public async Task KuudraBasic_WithoutCombat22_IsInaccessible()
    {
        var snapshot = Veteran();
        snapshot.SkillLevels["combat"] = 20;

        var result = await new KuudraT1Task().ExecuteWithRequirements(Params(snapshot));

        result.IsAccessible.Should().BeFalse();
        result.InaccessibleReason.Should().Be("Needs Combat 22 (you: 20)");
    }

    [Test]
    public async Task KuudraHot_NeverUnlocked_IsInaccessible_NamingBasicClear()
    {
        var snapshot = Veteran();
        snapshot.KuudraCompletions.Clear();

        var result = await new KuudraT2Task().ExecuteWithRequirements(Params(snapshot));

        result.IsAccessible.Should().BeFalse();
        result.InaccessibleReason.Should().Be("Needs Clear Kuudra Basic (you: none cleared)");
    }

    [TestCase(2, 999, false)]
    [TestCase(2, 1_000, true)]
    public async Task KuudraHot_NeedsReputation(int tier, int reputation, bool accessible)
    {
        var snapshot = Veteran();
        snapshot.KuudraReputation = reputation;

        var result = await new KuudraT2Task().ExecuteWithRequirements(Params(snapshot));

        result.IsAccessible.Should().Be(accessible);
        if (!accessible)
            result.InaccessibleReason.Should().StartWith("Needs 1,000 reputation");
    }

    [Test]
    public async Task KuudraInfernal_NeedsFieryClearedAnd12kReputation()
    {
        var snapshot = Veteran();
        snapshot.KuudraCompletions[3] = 2;
        snapshot.KuudraReputation = 20_000;

        var result = await new KuudraT5Task().ExecuteWithRequirements(Params(snapshot));
        result.InaccessibleReason.Should().Be("Needs Clear Kuudra Fiery (you: Burning cleared)");

        snapshot.KuudraCompletions[4] = 1;
        (await new KuudraT5Task().ExecuteWithRequirements(Params(snapshot))).IsAccessible.Should().BeTrue();
    }

    // ── Slayers ──

    [Test]
    public async Task TarantulaT4_NeedsTier3OfTheSameBoss_NotJustTheUnlock()
    {
        var snapshot = Veteran();
        snapshot.Slayers["zombie"] = new() { HighestTierKilled = 2 };
        snapshot.Slayers["spider"] = new() { HighestTierKilled = 2 };

        var result = await new T4TarantulaTask().ExecuteWithRequirements(Params(snapshot));

        result.IsAccessible.Should().BeFalse();
        result.InaccessibleReason.Should().Be("Needs Kill Tarantula Broodfather tier 3 (you: tier 2 killed)");
        snapshot.Slayers["spider"].HighestTierKilled = 3;
        (await new T4TarantulaTask().ExecuteWithRequirements(Params(snapshot))).IsAccessible.Should().BeTrue();
    }

    [Test]
    public async Task BlazeSlayer_IsLockedUntilVoidgloomTier3_AndTheChainStartsAtRevenant()
    {
        var snapshot = Veteran();
        snapshot.Slayers["enderman"] = new() { HighestTierKilled = 2 };

        var result = await new BlazeSlayerTask().ExecuteWithRequirements(Params(snapshot));

        result.InaccessibleReason.Should().Be("Needs Kill Voidgloom Seraph tier 3 (you: tier 2 killed)");
        var sven = await new SvenSlayerTask().ExecuteWithRequirements(Params(snapshot));
        sven.InaccessibleReason.Should().Be("Needs Kill Tarantula Broodfather tier 2 (you: no kills)");
    }

    [Test]
    public async Task Voidgloom_RecommendedSlayerLevel_NeverBlocks()
    {
        var snapshot = Veteran();
        snapshot.Slayers["wolf"] = new() { HighestTierKilled = 4 };
        snapshot.Slayers["enderman"] = new() { HighestTierKilled = 3, Level = 5 };

        var result = await new T4VoidgloomsTask().ExecuteWithRequirements(Params(snapshot));

        result.IsAccessible.Should().BeTrue();
        result.Breakdown.RequirementsMet.Should().BeTrue("only hard requirements count");
        var recommended = result.Breakdown.Requirements.Single(r => !r.Hard);
        recommended.Met.Should().BeFalse();
        recommended.CurrentText.Should().Be("5");
    }

    [Test]
    public void SlayerLevel7_NeedsHundredThousandXp()
    {
        PlayerProfileSnapshot.SlayerLevelFromXp("zombie", 99_999).Should().Be(6);
        PlayerProfileSnapshot.SlayerLevelFromXp("zombie", 100_000).Should().Be(7);
    }

    // ── Areas ──

    [Test]
    public async Task CrystalHollows_NeedsHotm3_ModTierWinsOverTheProfile()
    {
        var snapshot = Veteran();
        snapshot.HotmTier = 2;

        var blocked = await new CrystalHollowsTask().ExecuteWithRequirements(Params(snapshot));
        blocked.InaccessibleReason.Should().Be("Needs Heart of the Mountain 3 (you: 2)");

        var parameters = Params(snapshot);
        parameters.ExtractedInfo.HeartOfTheMountain = new HeartOfThe { Tier = 3 };
        (await new CrystalHollowsTask().ExecuteWithRequirements(parameters)).IsAccessible.Should().BeTrue("the Crystal Hollows pass needs HOTM 3, a HOTM 5 player gets in");
    }

    [Test]
    public void AreaTable_CoversTheListedIslandsAndZones()
    {
        string Labels(string zone) => string.Join(", ", AreaRequirements.ForZone(zone).Select(r => r.Label));

        Labels("The End").Should().Be("Combat 12");
        Labels("Smoldering Tomb").Should().Be("Combat 22");
        Labels("Dwarven Mines").Should().Be("Mining 12");
        Labels("Crystal Hollows").Should().Be("Mining 12, Heart of the Mountain 3");
        Labels("Glacite Tunnels").Should().Be("Mining 12, Heart of the Mountain 7");
        Labels("The Rift").Should().Be("SkyBlock level 12");
        Labels("The Garden").Should().Be("SkyBlock level 5");
        Labels("Driptoad Delve").Should().Be("Foraging 12");
        Labels("Torrhus Canyon").Should().Be("Foraging 12, Heart of the Forest 4");
        Labels("Backwater Bayou").Should().Be("Fishing 5");
        Labels("Hub").Should().BeEmpty();
    }

    [Test]
    public void AreaTable_MultiZoneTask_NeedsOnlyWhatEveryZoneNeeds()
    {
        // the Voidgloom task spans three End zones; the player only has to reach one of them
        AreaRequirements.ForZones(["Dragon's Nest", "Void Sepulture", "Zealot Bruiser Hideout"])
            .Select(r => r.Label).Should().Equal("Combat 12");
        AreaRequirements.ForZones(["Dragon's Nest", "Hub"]).Should().BeEmpty();
        AreaRequirements.ForZones(["Your Island"]).Should().BeEmpty("an ambiguous zone needs nothing we know of");
    }

    [Test]
    public async Task BackwaterBayou_WithTooLowFishing_IsInaccessible()
    {
        var snapshot = Veteran();
        snapshot.SkillLevels["fishing"] = 4;

        var result = await new BackwaterBayouTask().ExecuteWithRequirements(Params(snapshot));

        result.InaccessibleReason.Should().Be("Needs Fishing 5 (you: 4)");
    }

    // ── Combination with the existing gates ──

    /// <summary>A result the task's own gate (mayor, time window, daily) already marked inaccessible.</summary>
    private static TaskResult GatedResult() => new()
    {
        IsAccessible = false,
        InaccessibleReason = "Only available during Test mayor",
        NextAvailableAt = new DateTime(2030, 1, 1),
        Breakdown = new MethodBreakdown()
    };

    [Test]
    public void ExistingGate_StillApplies_WhenRequirementsAreMetOrUnknown()
    {
        foreach (var snapshot in new[] { Veteran(), null })
        {
            var result = GatedResult();
            RequirementEvaluator.Apply(result, [TaskRequirement.CatacombsLevel(36)], Params(snapshot));
            result.IsAccessible.Should().BeFalse();
            result.InaccessibleReason.Should().Be("Only available during Test mayor");
            result.NextAvailableAt.Should().Be(new DateTime(2030, 1, 1));
        }
    }

    [Test]
    public void ProgressionLock_WinsOverTheOtherGate_AndClearsTheTimeHint()
    {
        var snapshot = Veteran();
        snapshot.CatacombsLevel = 20;
        var result = GatedResult();

        RequirementEvaluator.Apply(result, [TaskRequirement.CatacombsLevel(36)], Params(snapshot));

        result.InaccessibleReason.Should().Be("Needs Catacombs 36 (you: 20)");
        result.NextAvailableAt.Should().BeNull();
    }

    [Test]
    public async Task DianaMayorGate_StillWorks_WithASnapshot()
    {
        var result = await new DianaTask().ExecuteWithRequirements(Params(Veteran(), mayor: "derpy"));

        result.IsAccessible.Should().BeFalse();
        result.InaccessibleReason.Should().Contain("Diana");
    }

    // ── Serialization and the whole catalog ──

    [Test]
    public async Task Breakdown_SerializesTheEvaluatedRequirements()
    {
        var snapshot = Veteran();
        snapshot.CatacombsLevel = 31;
        var result = await new M7Task().ExecuteWithRequirements(Params(snapshot));

        var json = JsonSerializer.Serialize(result.Breakdown, new JsonSerializerOptions { IncludeFields = true });
        using var doc = JsonDocument.Parse(json);
        var requirement = doc.RootElement.GetProperty("Requirements").EnumerateArray().Single(r => r.GetProperty("Label").GetString() == "Catacombs 36");

        requirement.GetProperty("Kind").GetString().Should().Be("catacombs_level");
        requirement.GetProperty("Target").GetInt32().Should().Be(36);
        requirement.GetProperty("Hard").GetBoolean().Should().BeTrue();
        requirement.GetProperty("Met").GetBoolean().Should().BeFalse();
        requirement.GetProperty("Current").GetDouble().Should().Be(31);
        doc.RootElement.GetProperty("RequirementsMet").GetBoolean().Should().BeFalse();
    }

    [Test]
    public async Task Breakdown_WithoutRequirements_LeavesTheFieldsNull()
    {
        var result = await new DianaTask().ExecuteWithRequirements(Params(Veteran()));

        result.Breakdown?.Requirements.Should().BeNull();
        result.Breakdown?.RequirementsMet.Should().BeNull();
    }

    [Test]
    public void EveryRegisteredTask_HasWellFormedRequirements()
    {
        foreach (var task in new TaskRegistry().PublicTasks)
        {
            var requirements = task.AllRequirements;
            foreach (var requirement in requirements)
            {
                requirement.Kind.Should().NotBeNullOrEmpty(task.Name);
                requirement.Label.Should().NotBeNullOrEmpty(task.Name);
                requirement.Target.Should().BeGreaterThan(0, task.Name);
            }
        }
    }
}
