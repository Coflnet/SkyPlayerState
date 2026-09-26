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
    private static readonly string[] ExpectedRegisteredTaskNames =
    [
        "AmberMiningTask", "AshfangTask", "AutomatonTask", "BackwaterBayouTask", "BambuleafTask",
        "BarbarianDukeXTask", "BayouFishingHuntingTask", "BayouFishingTask", "BayouHotspotFishingHuntingTask",
        "BayouHotspotFishingTask", "BezalHuntingTask", "BladesoulTask", "BrownMushroomTask", "BruiserHuntingTask",
        "BurningsoulTask", "CoalMiningTask", "CobblestoneMiningTask", "ComposterTask", "CoralotTask",
        "CrimsonFishingHuntingTask", "CrimsonFishingTask", "CrimsonHotspotFishingTask", "CrimsonIsleTask",
        "DailyCrimsonQuestsTask", "DeepCavernsTask", "DiamondMiningTask", "DianaHuntingTask", "DianaTask",
        "DreadwingTask", "DrownedTask", "DwarvenMinesMiningTask", "ExperimentationTableTask",
        "FlamingSpiderHuntingTask", "FlamingWormFishingTask", "FlareHuntingTask", "ForgeTask",
        "GalateaDivingTask", "GalateaFishingHuntingTask", "GalateaFishingMethodTask", "GalateaFishingTask",
        "GalateaTask", "GardenTask", "GauntletOfContagionTask", "GhostHuntingTask", "GoblinHoldoutPowderMiningTask",
        "GoldenGhoulTask", "GoldMineTask", "HellwispHuntingTask", "HideonleafTask", "HuntingTrapTask",
        "InvisibugHuntingTask", "JadeMiningTask", "JasperMiningTask", "JerryTask", "JoydiveTask",
        "JunglePowderMiningTask", "KadaKnightHuntingTask", "KatTask", "KuudraT1Task", "KuudraT2Task",
        "KuudraT3Task", "KuudraT4Task", "KuudraT5Task", "LotusAtollTask", "LumisquidTask", "M4Task", "M5Task",
        "M6Task", "M7KismetTask", "M7Task", "MagmaCoreFishingTask", "MatchoTask", "MithrilDepositsPowderMiningTask",
        "MochibearkTask", "MossybitTask", "MyceliumTask", "NucleusMiningTask", "OasisFishingTask",
        "ObsidianDefenderHuntingTask", "ObsidianMiningTask", "PeridotMiningTask", "PestHuntingTask", "PestTask",
        "PolarvoidBookTask", "PrecursorCityPowderMiningTask", "RainSlimeHuntingTask", "ReaperScytheTask",
        "RedMushroomTask", "RedstoneMiningTask", "SapphireMiningTask", "ScathaMiningTask", "SeerTask",
        "ShellwiseTask", "ShimmeringLightSlippersTask", "SludgeMiningGemMixtureTask", "SludgeMiningTask",
        "SpikeTask", "SpookyFishingTask", "SquidFishingHuntingTask", "SquidFishingTask", "StarSentryTask",
        "T3InfernoDemonlordTask", "T4InfernoDemonlordTask", "T4TarantulaTask", "T4VoidgloomsFdTask",
        "T4VoidgloomsTask", "T5TarantulaTask", "TheEndTask", "TheParkTask", "ThystMiningTask",
        "TungstenMiningTask", "UmberMiningTask", "ViperShardNpcFlipTask", "VoraciousSpiderTask",
        "WaterFishingHuntingTask", "WaterFishingTask", "WaterWormFishingHuntingTask", "WaterWormFishingTask",
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
}
