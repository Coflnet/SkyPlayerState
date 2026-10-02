using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Pins the exact set of task class names TaskCatalog registers (moved here from SkyModCommands,
/// which used to keep its own duplicate task definitions and pinned the same list against them -
/// see the SkyUserState/SkyModCommands task-definition dedup). Catches a registration mistake
/// (a task silently dropped or renamed) that TaskCatalogTests' count/uniqueness checks alone would
/// not, since those stay green as long as the registered set is internally consistent.
/// </summary>
public class TaskCatalogRegistrationTests
{
    /// <summary>
    /// Hidden accounting tasks (HiddenTasks.cs, MethodTask.Hidden) - registered like any other task
    /// but excluded from every user-facing listing. Kept as its own explicit set so this test also
    /// pins WHICH registered tasks are hidden, not just that they're registered.
    /// </summary>
    private static readonly string[] ExpectedHiddenTaskNames =
    [
        "BazaarPurchaseTask", "AuctionPurchaseTask", "MinionCollectionTask",
        // hidden zone tasks for zones where no money making method exists - see HiddenTasks.cs
        "PrivateIslandActivityTask", "ForgeClaimsTask", "HubTradingTask", "RiftActivityTask", "UnknownLocationTask"
    ];

    private static readonly string[] ExpectedRegisteredTaskNames =
    [
        "AmberMiningTask", "AshfangTask", "AuctionPurchaseTask", "AutomatonTask", "BackwaterBayouTask", "BambuleafTask",
        "BarbarianDukeXTask", "BayouFishingHuntingTask", "BayouFishingTask", "BayouHotspotFishingHuntingTask",
        "BayouHotspotFishingTask", "BazaarPurchaseTask", "BezalHuntingTask", "BirriesHuntingTask", "BladesoulTask", "BlazeSlayerTask", "BrownMushroomTask", "BruiserHuntingTask",
        "BurningsoulTask", "CactusFarmingTask", "CarrotFarmingTask", "CoalMiningTask", "CobblestoneMiningTask",
        "CocoaBeansFarmingTask", "ComposterTask", "CoralotTask",
        "CrimsonFishingHuntingTask", "CrimsonFishingTask", "CrimsonHotspotFishingTask", "CrimsonIsleTask",
        "CritterSafariTask", "CrystalHollowsPowderMiningTask",
        "DailyCrimsonQuestsTask", "DeepCavernsTask", "DiamondMiningTask", "DianaHuntingTask", "DianaTask",
        "DreadwingTask", "DrownedTask", "DwarvenMinesMiningTask", "ExperimentationTableTask",
        "F1Task", "F2Task", "F3Task", "F4Task", "F5Task", "F6Task", "F7Task",
        "FigForagingTask", "FlamingSpiderHuntingTask", "FlamingWormFishingTask", "FlareHuntingTask", "FlintMiningTask", "ForgeClaimsTask", "ForgeTask",
        "GalateaDivingTask", "GalateaFishingHuntingTask", "GalateaFishingMethodTask", "GalateaFishingTask",
        "GalateaTask", "GardenTask", "GauntletOfContagionTask", "GhostHuntingTask", "GhostMistTask",
        "GlaciteMiningTask", "GlaciteWalkerHuntingTask", "GoblinFarmingTask", "GoblinHoldoutPowderMiningTask",
        "GoldenGhoulTask", "GoldMineTask", "HelixForagingTask", "HellwispHuntingTask", "HideonleafTask",
        "HoneycombGatheringTask", "HubTradingTask", "HuntingTrapTask",
        "InvisibugHuntingTask", "JadeMiningTask", "JasperMiningTask", "JerryTask", "JoydiveTask",
        "JunglePowderMiningTask", "KadaKnightHuntingTask", "KatTask", "KuudraT1Task", "KuudraT2Task",
        "KuudraT3Task", "KuudraT4Task", "KuudraT5Task", "KuudraChestClaimsTask", "HeavyPearlsTask", "GardenVisitorsTask", "RiftActivityTask", "UnknownLocationTask", "LotusAtollTask", "LumisquidTask",
        "M1Task", "M2Task", "M3Task", "M4Task", "M5Task",
        "M6Task", "M7KismetTask", "M7Task", "MageOutlawTask", "MagmaCoreFishingTask", "MangroveForagingTask", "MatchoTask", "MelonFarmingTask", "MithrilDepositsPowderMiningTask",
        "MinerZombieHuntingTask", "MinionCollectionTask", "MithrilMiningTask", "MochibearkTask", "MoonflowerFarmingTask", "MossybitTask", "MudwormHuntingTask", "MushroomFarmingTask", "MyceliumTask", "NetherWartFarmingTask", "NucleusMiningTask", "OasisFishingTask",
        "ObsidianDefenderHuntingTask", "ObsidianMiningTask", "PeridotMiningTask", "PestHuntingTask", "PestTask",
        "PolarvoidBookTask", "PotatoFarmingTask", "PrecursorCityPowderMiningTask", "PrivateIslandActivityTask", "PumpkinFarmingTask", "RainSlimeHuntingTask", "ReaperScytheTask",
        "RedMushroomTask", "RedstoneMiningTask", "RevenantSlayerTask", "SapphireMiningTask", "ScathaMiningTask", "SeerTask",
        "ShellwiseTask", "ShimmeringLightSlippersTask", "SludgeMiningGemMixtureTask", "SludgeMiningTask",
        "SpikeTask", "SpookyFishingTask", "SquidFishingHuntingTask", "SquidFishingTask", "StarSentryTask",
        "SugarCaneFarmingTask", "SunflowerFarmingTask", "SvenSlayerTask",
        "T3InfernoDemonlordTask", "T4InfernoDemonlordTask", "T4TarantulaTask", "T4VoidgloomsFdTask",
        "T4VoidgloomsTask", "T5TarantulaTask", "TarantulaSlayerCrimsonIsleTask", "TheEndTask", "TheParkTask", "ThystMiningTask",
        "TikiHuntingTask", "TreasureHoarderHuntingTask",
        "TungstenMiningTask", "UmberMiningTask", "ViperShardNpcFlipTask", "VoraciousSpiderTask",
        "WaterFishingHuntingTask", "WaterFishingTask", "WaterWormFishingHuntingTask", "WaterWormFishingTask",
        "WheatFarmingTask", "WildRoseFarmingTask",
        "WinterFishingTask", "WitherSpecterHuntingTask", "XyzHuntingTask", "YogHuntingTask", "ZealotHuntingTask",
        "ZealotsFdTask"
    ];

    [Test]
    public void TaskCatalog_RegistersTheSameTaskNames_AsBeforeTheDedup()
    {
        var registeredNames = TaskCatalog.Create().Values.Distinct().Select(t => t.GetType().Name).OrderBy(n => n).ToList();
        var expected = ExpectedRegisteredTaskNames.OrderBy(n => n).ToList();
        registeredNames.Should().BeEquivalentTo(expected,
            "the registered task set must stay identical to what SkyModCommands used to register "
            + "from its own (now deleted) duplicate copy");
    }

    /// <summary>
    /// Pins exactly which registered tasks are hidden (MethodTask.Hidden) - catches both a hidden
    /// task that was forgotten to be marked, and a real (non-hidden) task accidentally marked hidden.
    /// </summary>
    [Test]
    public void TaskCatalog_HiddenTasks_MatchExpectedSet()
    {
        var hiddenNames = TaskCatalog.Create().Values.Distinct()
            .OfType<MethodTask>().Where(t => t.IsHidden)
            .Select(t => t.GetType().Name).OrderBy(n => n).ToList();
        var expected = ExpectedHiddenTaskNames.OrderBy(n => n).ToList();
        hiddenNames.Should().BeEquivalentTo(expected,
            "hidden accounting tasks (HiddenTasks.cs) must never be shown to players; this test also "
            + "catches a real task accidentally marked Hidden");
    }
}
