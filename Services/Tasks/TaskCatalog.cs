using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

internal static class TaskCatalog
{
    /// <summary>
    /// Task classes that exist but are deliberately not offered to users,
    /// mostly because a more specific task supersedes them.
    /// Kept explicit so the completeness test catches genuinely forgotten registrations.
    /// </summary>
    internal static readonly HashSet<Type> IntentionallyUnregistered =
    [
        typeof(SlayerTask),             // superseded by per-tier tasks in SlayerTasks.cs
        typeof(CatacombsTask),          // superseded by M4-M7 dungeon tasks
        typeof(CrystalHollowsTask),     // superseded by gem/powder mining tasks
        typeof(DwarvenMinesTask),       // superseded by DwarvenMinesMiningTask
        typeof(CrimsonIsleFishingTask), // superseded by CrimsonFishingTask variants
        typeof(TheParkForagingTask),    // superseded by TheParkTask
        typeof(RiftTask),               // superseded by RiftAccessTask
        typeof(HubTask),                // not a money making area
        typeof(DungeonHubTask),         // not a money making area
        typeof(FarmingIslandsTask),     // superseded by GardenTask
        typeof(SpidersDenTask),         // superseded by tarantula slayer tasks

        // ── Unregistered 2026-07 after per-task web research found the data fabricated. ──
        // Kept as classes (not deleted) so they can be re-added once corrected. See task-verdicts.json.
        typeof(SporeTask),                  // no "Spore" mob / SHARD_SPORE exists on Galatea (fabricated)
        typeof(ExtremelyRealShurikenTask),  // real item (FAKE_SHURIKEN) but a Rift NPC purchase, not a forge craft
        typeof(SoulOfTheAlphaTask),         // "Soul of the Alpha" is a Wolf-slayer mob, not a forge-craftable item
        typeof(BladeSoulBzTask),            // BLADESOUL_FRAGMENT / BLADESOUL_BLADE do not exist
        typeof(AshfangBzTask),              // DERELICT_ASHE -> EMBER_ROD flip is fabricated (unrelated items)
        typeof(EmptyChumcapBucketTask),     // wrong input tag + items are AH-only, not a bazaar flip
        typeof(EndermanPetFdTask),          // NULL_SPHERE -> ENDERMAN_PET_ITEM fabricated; pets are not bz-craftable

        // ── Deactivated 2026-07: real activity exists but the definition is broken/uncertain. ──
        // Kept as classes so they can be corrected and re-added; see task-verdicts.json for the research.
        typeof(PiscaryFishingTask),         // "Piscary" is a rod enchantment, not a location; dup of Galatea fishing
        typeof(PiscaryFishingHuntingTask),  // same fabricated "Piscary" location
        typeof(QuarryFishingTask),          // "Abandoned Quarry" bestiary grind, not an efficient coin/h method
        typeof(QuarryFishingHuntingTask),   // same
        typeof(FestivalFishingTask),        // fabricated "Festival Plaza"; seasonal; shark drop tags unconfirmed
        typeof(FestivalFishingHuntingTask), // conflates the Fishing Festival with Tomb Floodway shard fishing
        typeof(SpookyFishingHuntingTask),   // conflates Spooky Festival fishing with shard hunting (different systems)
        typeof(WinterFishingHuntingTask),   // fishing nets (shard hunting) do not work at Jerry's Workshop
        typeof(OasisFishingHuntingTask),    // real activity is Tomb Floodway shard fishing, mislabeled as Oasis
        typeof(CinderbatTask),              // real mob but spawns on Crimson Isle not Galatea; SHARD_CINDER_BAT tag unconfirmed
        typeof(BurningsoulTask),            // shard farmed in Smoldering Tomb (Crimson Isle), not the Galatea ember zones
        typeof(StridersurferTask),          // real, but SHARD_STRIDER_SURFER tag id needs confirming
        typeof(XyzMobTask),                 // duplicate of XyzHuntingTask; wrong location (Exe mob is on Crimson Isle)
        typeof(GhostMobTask),               // duplicate of GhostHuntingTask; GHOST_COIN is not a real item id
        typeof(SludgeMiningCoalTask),       // fabricated "coal from sludge" variant; sludge is a Crystal Hollows grind
        typeof(FigTask),                    // Fig is Foraging (Galatea), not a Garden crop; FIG/ENCHANTED_FIG ids are fake
        typeof(ExportableCarrotsCraftTask), // recipe fabricated; inputs are Rift-only, not a bazaar flip
        typeof(ExportableCarrotsTask),      // same fabricated recipe as a bazaar craft
        typeof(GrandmasKnittingNeedleTask), // bought from a Rift NPC, not a forge craft
        typeof(BluetoothRingTask),          // Collections-menu craft, not a forge craft (ForgeCraftTask never matches)
        typeof(DiscriteTask),               // crafting-table Rift item, not a forge craft
        typeof(CaducousFeederTask),         // crafting-table Rift item, not a forge craft
        typeof(ShimmeringLightHoodTask),    // likely a crafting-table (Mycelium IX) craft, not a forge recipe
        typeof(RiftAccessTask),             // MOTES are non-transferable currency with no coin value
    ];

    internal static ClassNameDictonary<ProfitTask> Create()
    {
        var tasks = new ClassNameDictonary<ProfitTask>();

        // Core tasks (special logic)
        tasks.Add<KatTask>();
        tasks.Add<ForgeTask>();
        tasks.Add<ComposterTask>();

        // Generic location trackers
        tasks.Add<GalateaDivingTask>();
        tasks.Add<GalateaFishingTask>();
        tasks.Add<GalateaTask>();
        tasks.Add<JerryTask>();
        tasks.Add<GoldMineTask>();
        tasks.Add<DeepCavernsTask>();
        tasks.Add<DwarvenMinesMiningTask>();
        tasks.Add<TheEndTask>();
        tasks.Add<TheParkTask>();
        tasks.Add<BackwaterBayouTask>();
        tasks.Add<GardenTask>();
        tasks.Add<CrimsonIsleTask>();

        // Fishing tasks (regular)
        // tasks.Add<PiscaryFishingTask>();     // deactivated: "Piscary" is a rod enchantment, not a place
        tasks.Add<BayouFishingTask>();
        tasks.Add<BayouHotspotFishingTask>();
        tasks.Add<SpookyFishingTask>();
        tasks.Add<WinterFishingTask>();
        tasks.Add<WaterWormFishingTask>();
        // tasks.Add<QuarryFishingTask>();      // deactivated: bestiary grind, not an efficient money method
        tasks.Add<CrimsonFishingTask>();
        tasks.Add<CrimsonHotspotFishingTask>();
        // tasks.Add<FestivalFishingTask>();    // deactivated: fabricated location, seasonal, unconfirmed shark tags
        tasks.Add<SquidFishingTask>();
        tasks.Add<GalateaFishingMethodTask>();
        tasks.Add<OasisFishingTask>();
        tasks.Add<WaterFishingTask>();
        tasks.Add<MagmaCoreFishingTask>();
        tasks.Add<FlamingWormFishingTask>();

        // Fishing tasks (hunting)
        // tasks.Add<PiscaryFishingHuntingTask>();  // deactivated: fabricated "Piscary" location
        tasks.Add<BayouFishingHuntingTask>();
        tasks.Add<BayouHotspotFishingHuntingTask>();
        // tasks.Add<SpookyFishingHuntingTask>();   // deactivated: conflates Spooky fishing with shard hunting
        // tasks.Add<WinterFishingHuntingTask>();   // deactivated: shard hunting impossible at Jerry's Workshop
        tasks.Add<WaterWormFishingHuntingTask>();
        // tasks.Add<QuarryFishingHuntingTask>();   // deactivated: see QuarryFishingTask
        tasks.Add<CrimsonFishingHuntingTask>();
        // tasks.Add<FestivalFishingHuntingTask>(); // deactivated: conflates Festival with Tomb Floodway shard fishing
        tasks.Add<SquidFishingHuntingTask>();
        tasks.Add<GalateaFishingHuntingTask>();
        // tasks.Add<OasisFishingHuntingTask>();    // deactivated: real spot is Tomb Floodway, mislabeled as Oasis
        tasks.Add<WaterFishingHuntingTask>();

        // Kuudra tasks
        tasks.Add<KuudraT1Task>();
        tasks.Add<KuudraT2Task>();
        tasks.Add<KuudraT3Task>();
        tasks.Add<KuudraT4Task>();
        tasks.Add<KuudraT5Task>();

        // Slayer tasks
        tasks.Add<T3InfernoDemonlordTask>();
        tasks.Add<T4InfernoDemonlordTask>();
        tasks.Add<T5TarantulaTask>();
        tasks.Add<T4TarantulaTask>();
        tasks.Add<AshfangTask>();
        tasks.Add<BarbarianDukeXTask>();
        tasks.Add<T4VoidgloomsTask>();
        tasks.Add<T4VoidgloomsFdTask>();

        // Mob farm tasks (Galatea)
        // tasks.Add<CinderbatTask>();      // deactivated: Crimson Isle mob (not Galatea); SHARD_CINDER_BAT tag unconfirmed
        // tasks.Add<BurningsoulTask>();    // deactivated: farmed in Smoldering Tomb (Crimson Isle), not Galatea
        tasks.Add<LumisquidTask>();
        tasks.Add<ShellwiseTask>();
        tasks.Add<MatchoTask>();
        // tasks.Add<StridersurferTask>();  // deactivated: SHARD_STRIDER_SURFER tag id needs confirming
        // tasks.Add<SporeTask>();          // removed 2026-07: no such Galatea mob/shard (see IntentionallyUnregistered)
        tasks.Add<BladesoulTask>();
        tasks.Add<JoydiveTask>();
        tasks.Add<DrownedTask>();
        tasks.Add<CoralotTask>();
        tasks.Add<BambuleafTask>();
        tasks.Add<HideonleafTask>();
        tasks.Add<DreadwingTask>();
        tasks.Add<SpikeTask>();
        tasks.Add<SeerTask>();
        tasks.Add<MochibearkTask>();
        tasks.Add<MossybitTask>();

        // Mob farm tasks (non-Galatea)
        tasks.Add<VoraciousSpiderTask>();
        tasks.Add<GoldenGhoulTask>();
        tasks.Add<StarSentryTask>();
        tasks.Add<AutomatonTask>();
        // tasks.Add<XyzMobTask>();         // deactivated: duplicate of XyzHuntingTask, wrong location
        // tasks.Add<GhostMobTask>();       // deactivated: duplicate of GhostHuntingTask; GHOST_COIN is not a real item

        // Hunting tasks
        tasks.Add<RainSlimeHuntingTask>();
        tasks.Add<HellwispHuntingTask>();
        tasks.Add<XyzHuntingTask>();
        tasks.Add<KadaKnightHuntingTask>();
        tasks.Add<InvisibugHuntingTask>();
        tasks.Add<YogHuntingTask>();
        tasks.Add<FlareHuntingTask>();
        tasks.Add<BezalHuntingTask>();
        tasks.Add<GhostHuntingTask>();
        tasks.Add<FlamingSpiderHuntingTask>();
        tasks.Add<ObsidianDefenderHuntingTask>();
        tasks.Add<WitherSpecterHuntingTask>();
        tasks.Add<ZealotHuntingTask>();
        tasks.Add<BruiserHuntingTask>();
        tasks.Add<PestHuntingTask>();

        // Diana / Mythological event tasks
        tasks.Add<DianaTask>();
        tasks.Add<DianaHuntingTask>();

        // Mining tasks (gemstone)
        tasks.Add<ThystMiningTask>();
        tasks.Add<JasperMiningTask>();
        tasks.Add<JadeMiningTask>();
        tasks.Add<AmberMiningTask>();
        tasks.Add<SapphireMiningTask>();
        tasks.Add<PeridotMiningTask>();

        // Mining tasks (ore)
        tasks.Add<CoalMiningTask>();
        tasks.Add<DiamondMiningTask>();
        tasks.Add<RedstoneMiningTask>();
        tasks.Add<CobblestoneMiningTask>();
        tasks.Add<ObsidianMiningTask>();
        tasks.Add<TungstenMiningTask>();
        tasks.Add<UmberMiningTask>();

        // Mining tasks (special)
        tasks.Add<NucleusMiningTask>();
        tasks.Add<SludgeMiningTask>();
        tasks.Add<SludgeMiningGemMixtureTask>();
        // tasks.Add<SludgeMiningCoalTask>(); // deactivated: fabricated "coal from sludge" variant
        tasks.Add<ScathaMiningTask>();
        tasks.Add<PrecursorCityPowderMiningTask>();
        tasks.Add<JunglePowderMiningTask>();
        tasks.Add<MithrilDepositsPowderMiningTask>();
        tasks.Add<GoblinHoldoutPowderMiningTask>();

        // Crafting tasks
        tasks.Add<ReaperScytheTask>();
        tasks.Add<GauntletOfContagionTask>();
        // tasks.Add<ExportableCarrotsCraftTask>(); // deactivated: fabricated recipe, Rift-only inputs
        tasks.Add<ShimmeringLightSlippersTask>();
        // tasks.Add<ExtremelyRealShurikenTask>(); // removed 2026-07: Rift NPC item, not a forge craft
        // tasks.Add<ShimmeringLightHoodTask>();    // deactivated: likely a crafting-table (Mycelium IX) craft, not forge
        tasks.Add<PolarvoidBookTask>();
        // tasks.Add<GrandmasKnittingNeedleTask>(); // deactivated: bought from a Rift NPC, not a forge craft
        // tasks.Add<SoulOfTheAlphaTask>();        // removed 2026-07: it is a mob, not a forge-craftable item
        // tasks.Add<BluetoothRingTask>();          // deactivated: Collections-menu craft, not a forge craft
        // tasks.Add<DiscriteTask>();               // deactivated: crafting-table Rift item, not a forge craft
        // tasks.Add<CaducousFeederTask>();         // deactivated: crafting-table Rift item, not a forge craft

        // Bazaar crafting tasks
        // removed 2026-07: fabricated item tags / not real bazaar flips (see IntentionallyUnregistered)
        // tasks.Add<BladeSoulBzTask>();
        // tasks.Add<AshfangBzTask>();
        // tasks.Add<EmptyChumcapBucketTask>();
        // tasks.Add<EndermanPetFdTask>();
        // tasks.Add<ExportableCarrotsTask>();      // deactivated: fabricated CARROT_ITEM->EXPORTABLE recipe

        // Dungeon tasks
        tasks.Add<M4Task>();
        tasks.Add<M5Task>();
        tasks.Add<M6Task>();
        tasks.Add<M7Task>();
        tasks.Add<M7KismetTask>();

        // Garden tasks
        tasks.Add<PestTask>();
        // tasks.Add<FigTask>();            // deactivated: Fig is Foraging (Galatea), not a Garden crop; ids are fake

        // Misc tasks
        tasks.Add<ZealotsFdTask>();
        tasks.Add<RedMushroomTask>();
        tasks.Add<BrownMushroomTask>();
        tasks.Add<MyceliumTask>();

        // Passive tasks
        tasks.Add<HuntingTrapTask>();

        // Limited/daily tasks
        tasks.Add<DailyCrimsonQuestsTask>();
        tasks.Add<ExperimentationTableTask>();
        // tasks.Add<RiftAccessTask>();     // deactivated: MOTES are non-transferable, no coin value
        tasks.Add<ViperShardNpcFlipTask>();

        return tasks;
    }
}