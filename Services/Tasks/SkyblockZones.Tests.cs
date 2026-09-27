using System.Collections.Generic;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class SkyblockZonesTests
{
    // ── New zones (production scoreboard logs, player Ekwav) ──

    [TestCase("Builder's House")]
    [TestCase("Combat Settlement")]
    [TestCase("Communal Stew")]
    [TestCase("Fishing Outpost")]
    [TestCase("Foraging Camp")]
    public void HubSubZones_ResolveToHub(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Hub");
    }

    // ── Galatea archipelago: two separate islands (game owner correction, 2026-09) ──
    // Moonglade Marsh (default, /warp galatea) and Torrhus Canyon (/warp torrhus) are distinct
    // islands with essentially disjoint content - verified zone-by-zone against
    // hypixelskyblock.minecraft.wiki. Island keys are "Moonglade"/"Torrhus" (never a zone name -
    // see the design comment in SkyblockZones.BuildMap) so a task listing the zone
    // "Moonglade Marsh"/"Torrhus Canyon" specifically never silently expands to the whole island.

    [TestCase("Moonglade Marsh")]
    [TestCase("Tangleburg")]
    [TestCase("Murkwater Loch")]
    [TestCase("Wyrmgrove Tomb")]
    public void MoonglandeZones_ResolveToMoonglade(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Moonglade");
    }

    [TestCase("Torrhus Canyon")]
    [TestCase("Torrhus Heights")]
    [TestCase("Miria's Hut")]
    [TestCase("Critter Safari Entrance")]
    [TestCase("Torrhus Springs")]
    public void TorrhusZones_ResolveToTorrhus(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Torrhus");
    }

    [Test]
    public void GroupOf_BothGalateaIslands_IsGalatea()
    {
        SkyblockZones.GroupOf("Moonglade").Should().Be("Galatea");
        SkyblockZones.GroupOf("Torrhus").Should().Be("Galatea");
    }

    [Test]
    public void GroupOf_NonGroupedIsland_IsNull()
    {
        SkyblockZones.GroupOf("Hub").Should().BeNull();
        SkyblockZones.GroupOf("Crystal Hollows").Should().BeNull();
        SkyblockZones.GroupOf(null).Should().BeNull();
    }

    [Test]
    public void Matches_GalateaGroupLocation_MatchesEitherIsland()
    {
        var taskLocations = new HashSet<string> { "Galatea" };
        SkyblockZones.Matches(taskLocations, "Tangleburg").Should().BeTrue("Tangleburg is on Moonglade, part of the Galatea group");
        SkyblockZones.Matches(taskLocations, "Torrhus Heights").Should().BeTrue("Torrhus Heights is on Torrhus, part of the Galatea group");
    }

    [Test]
    public void Matches_MoonglandeOnlyLocation_DoesNotMatchTorrhusZone()
    {
        var taskLocations = new HashSet<string> { "Moonglade" };
        SkyblockZones.Matches(taskLocations, "Torrhus Canyon").Should().BeFalse();
        SkyblockZones.Matches(taskLocations, "Torrhus Heights").Should().BeFalse();
    }

    [Test]
    public void Matches_TorrhusOnlyLocation_DoesNotMatchMoonglandeZone()
    {
        var taskLocations = new HashSet<string> { "Torrhus" };
        SkyblockZones.Matches(taskLocations, "Moonglade Marsh").Should().BeFalse();
        SkyblockZones.Matches(taskLocations, "Tangleburg").Should().BeFalse();
    }

    /// <summary>
    /// Regression: several real tasks (HuntingTasks.cs, HuntingTrapTask.cs, MobFarmTasks.cs) list
    /// the literal zone "Moonglade Marsh" as one specific spot among a few unrelated zones - this
    /// must never be treated as "the whole Moonglade island" (which would happen if the island key
    /// equalled the zone name, since IslandOf(anyMoonglandeZone) would then equal a string the task
    /// Locations set already contains).
    /// </summary>
    [Test]
    public void Matches_MoonglandeMarshZoneLevelLocation_DoesNotExpandToWholeIsland()
    {
        var taskLocations = new HashSet<string> { "Moonglade Marsh" };
        SkyblockZones.Matches(taskLocations, "Moonglade Marsh").Should().BeTrue("the exact zone itself must still match");
        SkyblockZones.Matches(taskLocations, "Tangleburg").Should().BeFalse(
            "a task listing only the specific zone \"Moonglade Marsh\" must not silently match every other Moonglade zone");
        SkyblockZones.Matches(taskLocations, "Murkwater Loch").Should().BeFalse();
    }

    [Test]
    public void Matches_TorrhusCanyonZoneLevelLocation_DoesNotExpandToWholeIsland()
    {
        var taskLocations = new HashSet<string> { "Torrhus Canyon" };
        SkyblockZones.Matches(taskLocations, "Torrhus Canyon").Should().BeTrue();
        SkyblockZones.Matches(taskLocations, "Torrhus Heights").Should().BeFalse(
            "a task listing only the specific zone \"Torrhus Canyon\" must not silently match every other Torrhus zone");
    }

    // ── The Farming Islands: separate island from Crimson Isle (was wrongly nested there before
    // this fix) - Oasis/Mushroom Desert/Glowing Mushroom Cave/Mushroom Gorge etc. are Farming
    // Islands zones, not Crimson Isle, despite both being desert-themed. ──

    [TestCase("Oasis")]
    [TestCase("Mushroom Desert")]
    [TestCase("Glowing Mushroom Cave")]
    [TestCase("Mushroom Gorge")]
    [TestCase("The Barn")]
    [TestCase("Trapper's Den")]
    [TestCase("Treasure Hunter Camp")]
    public void FarmingIslandsZones_ResolveToFarmingIslands_NotCrimsonIsle(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("The Farming Islands");
    }

    [Test]
    public void Matches_CrimsonIsleLocation_DoesNotMatchOasis()
    {
        var taskLocations = new HashSet<string> { "Crimson Isle" };
        SkyblockZones.Matches(taskLocations, "Oasis").Should().BeFalse(
            "Oasis is a Farming Islands zone, not Crimson Isle, even though both are desert-themed");
    }

    [Test]
    public void MagmaChamber_StaysUnderCrimsonIsle()
    {
        // Regression guard for the opposite mistake: Magma Chamber is a real Crimson Isle zone
        // (verified hypixelskyblock.minecraft.wiki/w/Magma_Chamber) and must not get swept into
        // The Farming Islands alongside the zones that actually needed to move.
        SkyblockZones.IslandOf("Magma Chamber").Should().Be("Crimson Isle");
    }

    // ── Lotus Atoll: its own separate island, not a Galatea zone (was wrongly nested before this fix) ──

    [TestCase("Lotus Atoll")]
    [TestCase("Lotus Eater's Cave")]
    [TestCase("Lotus Highlands")]
    [TestCase("Tewtil Tunnel")]
    public void LotusAtollZones_ResolveToLotusAtoll_NotGalatea(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Lotus Atoll");
        SkyblockZones.GroupOf("Lotus Atoll").Should().BeNull("Lotus Atoll is not part of the Galatea group");
    }

    // ── NormalizeTabArea (tab list "Area:" -> canonical island, see TabListUpdate) ──

    [TestCase("Moonglade Marsh")]
    public void NormalizeTabArea_MoonglandeSubRegion_ReturnsMoonglade(string tabArea)
    {
        SkyblockZones.NormalizeTabArea(tabArea).Should().Be("Moonglade");
    }

    [TestCase("Torrhus Canyon")]
    [TestCase("Torrhus Heights")]
    [TestCase("Miria's Hut")]
    [TestCase("Critter Safari Entrance")]
    public void NormalizeTabArea_TorrhusSubRegion_ReturnsTorrhus(string tabArea)
    {
        SkyblockZones.NormalizeTabArea(tabArea).Should().Be("Torrhus");
    }

    [Test]
    public void NormalizeTabArea_PrivateIsland_PassesThroughUnchanged()
    {
        // Distinct from the scoreboard's separately-ambiguous "Your Island" (see AmbiguousZones);
        // task Locations use the literal string "Private Island" (e.g. MyceliumTask), so
        // NormalizeTabArea must not rewrite it to anything else.
        SkyblockZones.NormalizeTabArea("Private Island").Should().Be("Private Island");
    }

    [TestCase("Hub")]
    [TestCase("Crystal Hollows")]
    [TestCase("Crimson Isle")]
    public void NormalizeTabArea_RegularIsland_PassesThroughUnchanged(string tabArea)
    {
        // Most islands' tab Area line already reports the island name itself - NormalizeTabArea
        // must be a no-op for them (it only needs to translate Galatea's sub-regions).
        SkyblockZones.NormalizeTabArea(tabArea).Should().Be(tabArea);
    }

    [Test]
    public void NormalizeTabArea_UnknownValue_PassesThroughUnchanged()
    {
        SkyblockZones.NormalizeTabArea("Some Future Island").Should().Be("Some Future Island");
    }

    [Test]
    public void NormalizeTabArea_NullOrEmpty_ReturnsAsIs()
    {
        SkyblockZones.NormalizeTabArea(null).Should().BeNull();
        SkyblockZones.NormalizeTabArea("").Should().Be("");
    }

    // ── Canonical() / real production scoreboard zone strings (2026-09 "80% of coin value went
    // unclassified" investigation) - format codes, private-use-area glyphs, doubled spaces, the
    // Garden pest-count suffix, and free-text plot names. ──

    [Test]
    public void Canonical_Null_ReturnsNull()
    {
        SkyblockZones.Canonical(null).Should().BeNull();
    }

    [TestCase("The Garden  x3")]
    [TestCase("The Garden x1")]
    public void GardenWithPestCount_ResolvesToGardenIsland(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Garden");
    }

    [Test]
    public void GardenWithFormatCode_ResolvesToGardenIsland()
    {
        // "§l" is a Minecraft bold format code (section sign + one char), not part of the zone name.
        SkyblockZones.IslandOf("The Garden §l x1").Should().Be("Garden");
    }

    [TestCase("Plot - 8 ")]
    [TestCase("Plot - 2")]
    [TestCase("Plot - Left Farm 2")]
    [TestCase("Plot - Greenhouse")]
    [TestCase("Plot - CARROTS")]
    public void RenamedPlot_ResolvesToGardenIsland(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Garden");
    }

    [TestCase("The Catacombs (M7)")]
    [TestCase("The Catacombs (M4)")]
    [TestCase("The Catacombs (F1)")]
    [TestCase("The Catacombs (F7)")]
    [TestCase("The Catacombs (E)")]
    public void CatacombsFloor_ResolvesToDungeonHub(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Dungeon Hub");
    }

    [TestCase("Kuudra's Hollow (T1)")]
    [TestCase("Kuudra's Hollow (T4)")]
    [TestCase("Kuudra's Hollow (T5)")]
    public void KuudraTier_ResolvesToKuudra(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Kuudra");
    }

    [TestCase("Dwarven Base Camp")]
    [TestCase("Fossil Research Center")]
    [TestCase("Divan's Gateway")]
    [TestCase("Goblin Burrows")]
    [TestCase("Abandoned Quarry")]
    [TestCase("Aristocrat Passage")]
    public void MissingDwarvenMinesZones_ResolveToDwarvenMines(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Dwarven Mines");
    }

    [TestCase("Mining District")]
    [TestCase("Archery Range")]
    [TestCase("Election Room")]
    [TestCase("Sewer")]
    [TestCase("Catacombs Entrance")]
    [TestCase("Artist's Abode")]
    [TestCase("Shen's Auction")]
    [TestCase("Taylor's Shop")]
    public void MissingHubZones_ResolveToHub(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Hub");
    }

    [TestCase("Crimson Fields")]
    [TestCase("The Wasteland")]
    [TestCase("Plhlegblast Pool")]
    [TestCase("Dragontail Townsquare")]
    public void MissingCrimsonIsleZones_ResolveToCrimsonIsle(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("Crimson Isle");
    }

    [TestCase("Soul Cave")]
    [TestCase("Spirit Cave")]
    [TestCase("Trials of Fire")]
    public void MissingParkZones_ResolveToThePark(string zone)
    {
        SkyblockZones.IslandOf(zone).Should().Be("The Park");
    }

    [Test]
    public void CritterSafari_ResolvesToTorrhus()
    {
        SkyblockZones.IslandOf("Critter Safari").Should().Be("Torrhus");
    }

    [Test]
    public void Matches_GardenIslandLocation_MatchesRenamedPlot()
    {
        var taskLocations = new HashSet<string> { "Garden" };
        SkyblockZones.Matches(taskLocations, "Plot - CARROTS").Should().BeTrue();
    }

    [Test]
    public void Matches_TheGardenLocation_MatchesPestCountReading()
    {
        var taskLocations = new HashSet<string> { "The Garden" };
        SkyblockZones.Matches(taskLocations, "The Garden  x2").Should().BeTrue();
    }

    [Test]
    public void AmbiguousZone_WithFormatCodeNoise_StillResolvesNull()
    {
        // Canonicalizing must not accidentally "fix" an intentionally ambiguous zone.
        SkyblockZones.IslandOf("Wizard Tower").Should().BeNull();
        SkyblockZones.IslandOf("Community Center").Should().BeNull();
    }
}
