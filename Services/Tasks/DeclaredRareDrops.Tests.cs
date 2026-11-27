using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// The rare pool only takes expensive items a task declares (<see cref="TaskPeriodFolder.UndeclaredExpensive"/>). These pin
/// that the legit rare drops of the rare-heavy tasks are declared, so the gate does not under-value them.
/// </summary>
public class DeclaredRareDropsTests
{
    private static readonly TaskRegistry Registry = new();
    private const double Expensive = 100_000_000;

    private static MethodTask Task(string name) => (MethodTask)Registry.GetByName(name);

    /// <summary>True when a single <paramref name="tag"/> drop reaches the rare pool of <paramref name="task"/>.</summary>
    private static bool ReachesRarePool(MethodTask task, string tag)
    {
        var owned = TaskPeriodFolder.FilterOwnedItems(new() { { tag, 1 } }, task);
        var undeclared = TaskPeriodFolder.UndeclaredExpensive(owned, task, _ => Expensive);
        var kept = owned.Where(e => !undeclared.ContainsKey(e.Key)).ToDictionary(e => e.Key, e => e.Value);
        return TaskPeriodFolder.SplitRareAndCommon(kept, _ => Expensive, _ => true).RareItemCoins.ContainsKey(tag);
    }

    [TestCase("F7", "NECRON_HANDLE")]
    [TestCase("F7", "IMPLOSION_SCROLL")]
    [TestCase("F7", "ENCHANTMENT_ULTIMATE_ONE_FOR_ALL_1")]
    [TestCase("M7", "NECRON_HANDLE")]
    [TestCase("M7", "FIFTH_MASTER_STAR")]
    [TestCase("M7", "DARK_CLAYMORE")]
    [TestCase("M6", "GIANT_FRAGMENT_LASER")]
    [TestCase("F6", "GIANTS_SWORD")]
    [TestCase("F5", "SHADOW_FURY")]
    [TestCase("M4", "SECOND_MASTER_STAR")]
    [TestCase("M3", "FIRST_MASTER_STAR")]
    [TestCase("F1", "RECOMBOBULATOR_3000")]
    [TestCase("F3", "ENCHANTMENT_ULTIMATE_BANK_2")]
    [TestCase("Kuudra T5", "ENCHANTMENT_ULTIMATE_FATAL_TEMPO_1")]
    [TestCase("Kuudra T5", "INFERNAL_TERROR_CHESTPLATE")]
    [TestCase("Kuudra T5", "HOLLOW_WAND")]
    [TestCase("Kuudra T4", "SHARD_MOLTHORN")]
    [TestCase("Kuudra T2", "KUUDRA_TENTACLE")]
    [TestCase("Diana", "ENCHANTMENT_ULTIMATE_CHIMERA_1")]
    [TestCase("Diana", "MINOS_RELIC")]
    [TestCase("Diana", "DAEDALUS_STICK")]
    [TestCase("Revenant Slayer", "SCYTHE_BLADE")]
    [TestCase("Revenant Slayer", "WARDEN_HEART")]
    [TestCase("Revenant Slayer", "SEVERED_HAND")]
    [TestCase("Sven Slayer", "OVERFLUX_CAPACITOR")]
    [TestCase("Tarantula Slayer", "TARANTULA_TALISMAN")]
    [TestCase("Tarantula Slayer (Crimson Isle)", "ENSNARED_SNAIL")]
    [TestCase("T4 Voidglooms", "JUDGEMENT_CORE")]
    [TestCase("T4 Voidglooms (FD)", "ENDSTONE_IDOL")]
    [TestCase("Blaze Slayer", "HIGH_CLASS_ARCHFIEND_DICE")]
    [TestCase("Blaze Slayer", "ENCHANTMENT_SMOLDERING_6")]
    [TestCase("Crimson Fishing", "RADIOACTIVE_VIAL")]
    [TestCase("Crimson Fishing", "MAGMA_LORD_FRAGMENT")]
    [TestCase("Water Fishing", "GREAT_WHITE_SHARK_TOOTH")]
    [TestCase("Winter Fishing", "PET_BABY_YETI")]
    [TestCase("Spooky Fishing", "DEEP_SEA_ORB")]
    [TestCase("Ender Dragon", "PET_ENDER_DRAGON")]
    [TestCase("Ender Dragon", "ASPECT_OF_THE_DRAGON")]
    [TestCase("Glacite Mineshafts", "TUNGSTEN_KEY")]
    [TestCase("Glacite Mineshafts", "SKELETON_KEY")]
    [TestCase("Lotus Atoll", "LOTUS_DIAMOND")]
    [TestCase("Garden Visitors", "ENCHANTMENT_DEDICATION_4")]
    [TestCase("Garden Visitors", "OVERCLOCKER_3000")]
    [TestCase("Pest", "VINYL_BEETLE")]
    [TestCase("Helix Foraging", "HELIXIS")]
    [TestCase("Helix Honey Lathering", "MIRIA_PRIZE")]
    [TestCase("Arachne", "DARK_QUEENS_SOUL_DROP")]
    // from the 7 day production audit (2026-11-27)
    [TestCase("F7", "GIANT_FRAGMENT_LASER")]
    [TestCase("F7", "ICE_SPRAY_WAND")]
    [TestCase("F5", "GENERAL_MEDALLION")]
    [TestCase("M5", "MASTER_SKULL_TIER_3")]
    [TestCase("M7", "TREASURE_TALISMAN")]
    [TestCase("F4", "TREASURE_TALISMAN")]
    [TestCase("M5", "DYE_BONE")]
    [TestCase("F5", "DYE_SECRET")]
    [TestCase("M6", "ENCHANTMENT_ULTIMATE_LEGION_1")]
    [TestCase("Kuudra T5", "ENCHANTMENT_STRONG_MANA_5")]
    [TestCase("Kuudra T5", "TORMENTOR")]
    [TestCase("Kuudra T3", "MANDRAA")]
    [TestCase("Revenant Slayer", "SHARD_PARAGON")]
    [TestCase("Blaze Slayer", "SUBZERO_INVERTER")]
    [TestCase("Crimson Fishing (Hunting)", "SINGED_POWDER")]
    [TestCase("Crimson Fishing", "ENCHANTMENT_MAGMARIZER_6")]
    [TestCase("Bayou Fishing (Hunting)", "TITANOBOA_SHED")]
    [TestCase("Bayou Fishing", "MOBY_DUCK")]
    [TestCase("Spooky Fishing", "SHREDDED_LINE")]
    [TestCase("Moonglade Sea Creatures (Hunting)", "DIVER_FRAGMENT")]
    [TestCase("Lotus Atoll", "PRINCE_CROWN_JEWEL")]
    [TestCase("Ruby Mining", "FLAWLESS_RUBY_GEM")]
    [TestCase("Thyst Mining", "PERFECT_AMETHYST_GEM")]
    [TestCase("Goblin Holdout Powder Mining", "GOBLIN_EGG_BLUE")]
    [TestCase("Crystal Hollows Powder Mining", "JADERALD")]
    [TestCase("Experimentation Table", "ENCHANTMENT_GROWTH_6")]
    [TestCase("Garden Visitors", "POTION_harvest_harbinger")]
    // RequireShardItems: the fishing "(Hunting)" variants keep any sea creature shard (Thunder, Jawbus, ...)
    [TestCase("Crimson Fishing (Hunting)", "SHARD_THUNDER")]
    [TestCase("Winter Fishing (Hunting)", "SHARD_REINDRAKE")]
    public void KnownRareDrop_ReachesTheRarePool(string task, string tag)
        => ReachesRarePool(Task(task), tag).Should().BeTrue($"{tag} is a legit rare drop of {task}");

    [TestCase("Helix Honey Lathering", "ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3")]
    [TestCase("Helix Honey Lathering", "BEHEMOTH_POT_OF_HONEYCOMB")]
    [TestCase("F1", "NECRON_HANDLE")]
    [TestCase("Water Fishing", "HYPERION")]
    [TestCase("Kuudra T5", "ENCHANTMENT_ULTIMATE_CHIMERA_1")]
    // artifacts of the audit: bought, moved or another activity's loot
    [TestCase("Mangrove Foraging", "DIVER_FRAGMENT")]
    [TestCase("Experimentation Table", "ENCHANTMENT_GIANT_KILLER_7")]
    [TestCase("Garden Visitors", "VISITORS_GRATITUDE")]
    [TestCase("Glacite Mineshafts", "PERSONAL_COMPACTOR_MINING_4000")]
    [TestCase("Ender Dragon", "SUMMONING_EYE")]
    [TestCase("Water Fishing", "SHARD_THUNDER")]
    public void ForeignExpensiveItem_NeverReachesTheRarePool(string task, string tag)
        => ReachesRarePool(Task(task), tag).Should().BeFalse();

    /// <summary>
    /// Every declared drop of every public task passes the owned filter and the gate: no other filter silently takes a
    /// declared rare drop away. Not checked: the task's own supplies and Forge outputs it does not drop by formula (both
    /// deliberately no loot, see <see cref="MethodTask.IsSupplyTag"/> and <see cref="TaskPeriodFolder.IsForeignForgeOutput"/>).
    /// </summary>
    [Test]
    public void EveryDeclaredDrop_OfEveryPublicTask_ReachesTheRarePool()
    {
        var blocked = Registry.PublicMethodTasks
            .SelectMany(task => task.DeclaredDrops()
                .Where(tag => !task.IsSupplyTag(tag, task.KeepableCostTags()))
                .Where(tag => !TaskPeriodFolder.IsForeignForgeOutput(tag, task))
                .Where(tag => !ReachesRarePool(task, tag))
                .Select(tag => $"{task.GetDetectionSignature().MethodName}: {tag}"))
            .ToList();

        blocked.Should().BeEmpty();
    }

    [Test]
    public void HiddenAccountingTask_KeepsEverything()
        => TaskPeriodFolder.UndeclaredExpensive(new() { { "HYPERION", 1 } }, Task("Auction Purchases"), _ => Expensive)
            .Should().BeEmpty();

    [Test]
    public void CheapUndeclaredItem_IsKept()
        => TaskPeriodFolder.UndeclaredExpensive(new() { { "ICE_BAIT", 30 } }, Task("Helix Honey Lathering"), _ => 10)
            .Should().BeEmpty();

    [Test]
    public void EveryMasterAndNormalFloor_DeclaresItsChestLoot()
    {
        foreach (var floor in Enumerable.Range(1, 7).SelectMany(f => new[] { $"F{f}", $"M{f}" }))
            Task(floor).DeclaredDrops().Should().Contain("RECOMBOBULATOR_3000", floor);
    }

    /// <summary>The declared loot tables use real item ids (NotEnoughUpdates-REPO next to this repo; ignored without it).</summary>
    [Test]
    public void DeclaredLootTags_ExistAsNotEnoughUpdatesItems()
    {
        var itemsDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "NotEnoughUpdates-REPO", "items"));
        if (!Directory.Exists(itemsDir))
        {
            Assert.Ignore($"NotEnoughUpdates-REPO/items not found at {itemsDir}");
            return;
        }
        var tables = Enumerable.Range(1, 7).SelectMany(f => new[] { $"F{f}", $"M{f}" }).SelectMany(DeclaredLoot.CatacombsFloor)
            .Concat(new[] { "Revenant Horror", "Tarantula Broodfather", "Sven Packmaster", "Voidgloom Seraph", "Inferno Demonlord" }
                .SelectMany(DeclaredLoot.Slayer))
            .Concat(DeclaredLoot.KuudraChest).Concat(DeclaredLoot.LavaSeaCreatures).Concat(DeclaredLoot.BayouSeaCreatures)
            .Concat(DeclaredLoot.SpookySeaCreatures)
            // essence is a virtual item; NEU lists the hunting shards under other ids (production periods carry the SHARD_ tags)
            .Where(tag => tag != "ESSENCE_CRIMSON" && !tag.StartsWith("SHARD_"));
        var missing = tables.Distinct().Where(tag => !NeuFiles(tag).Any(f => File.Exists(Path.Combine(itemsDir, f)))).ToList();

        missing.Should().BeEmpty();
    }

    /// <summary>NEU file names of a tag: enchanted books are "NAME;level", pets "NAME;rarity".</summary>
    private static IEnumerable<string> NeuFiles(string tag)
    {
        yield return tag + ".json";
        if (tag.StartsWith("ENCHANTMENT_"))
        {
            var cut = tag.LastIndexOf('_');
            yield return $"{tag["ENCHANTMENT_".Length..cut]};{tag[(cut + 1)..]}.json";
        }
        if (tag.StartsWith("PET_"))
            foreach (var rarity in Enumerable.Range(0, 6))
                yield return $"{tag["PET_".Length..]};{rarity}.json";
    }

    // ── itemized rare pool in the estimate ──

    [Test]
    public void RareDrops_ExplainTheRarePartOfTheRate()
    {
        var aggregates = new TaskAggregateService(new Moq.Mock<global::Cassandra.ISession>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskAggregateService>.Instance);
        aggregates.AddContribution("Diana", 1, "p1", 3600, new() { ["GRIFFIN_FEATHER"] = 100 }, 0, 50_000_000, 50_100_000,
            new() { ["GRIFFIN_FEATHER"] = 100_000 }, null, rareItemCoins: new() { ["MINOS_RELIC"] = 30_000_000, ["DAEDALUS_STICK"] = 20_000_000 });
        var row = aggregates.PendingRowForTest("Diana", 1);
        TaskAggregateService.MergeIntoRow(row, new TaskBucketAggregateRow { WSeconds = 3600, RareCoins = 10_000_000,
            RareItemCoins = new() { ["MINOS_RELIC"] = 10_000_000 } }, TaskAggregateService.RowItemLimit);
        var agg = new BucketAggregate();
        TaskAggregateService.AddRow(agg, row, 1);
        var prices = new Dictionary<string, double> { ["GRIFFIN_FEATHER"] = 1000, ["MINOS_RELIC"] = 20_000_000, ["DAEDALUS_STICK"] = 5_000_000 };

        var parts = TaskEstimator.RateParts(Task("Diana"), agg, prices);
        var drops = TaskEstimator.BuildRareDrops(Task("Diana"), agg, prices);

        agg.RareItemCoins.Should().BeEquivalentTo(new Dictionary<string, double> { ["MINOS_RELIC"] = 40_000_000, ["DAEDALUS_STICK"] = 20_000_000 });
        drops.Should().OnlyContain(d => d.Kind == TaskDropRate.RareKind);
        drops.Sum(d => d.ContributionPerHour).Should().BeApproximately(parts.ScaledRareCoins * parts.RareEvidenceScale / 2, 1e-3);
        drops.Single(d => d.ItemTag == "MINOS_RELIC").RatePerHour.Should().BeApproximately(40_000_000.0 * parts.DriftScale / 2 / 20_000_000, 1e-9);
    }

    [Test]
    public void RowWithoutRareItems_ListsNoRareDrops()
        => TaskEstimator.BuildRareDrops(Task("Diana"), new BucketAggregate { WSeconds = 3600, RareCoins = 5 }, new()).Should().BeEmpty();

    // ── logging ──

    private class CountingLogger : ILogger
    {
        public readonly List<string> Lines = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            => Lines.Add(formatter(state, exception));
    }

    [Test]
    public void UndeclaredDropLog_LogsOncePerHour_AndSumsWhatItHeldBack()
    {
        var logger = new CountingLogger();
        var now = new DateTime(2026, 11, 27, 12, 0, 0);
        var log = new UndeclaredDropLog(logger, () => now);

        log.Record("Helix Honey Lathering", "ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3", 26, 1_742_000_156, "p1");
        log.Record("Helix Honey Lathering", "ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3", 1, 67_000_006, "p2");
        log.Record("Helix Honey Lathering", "STUFFED_CHILI_PEPPER", 16, 342_400_000, "p1");
        now = now.AddHours(1);
        log.Record("Helix Honey Lathering", "ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3", 2, 134_000_012, "p3");

        logger.Lines.Should().HaveCount(3);
        logger.Lines[0].Should().Contain("Helix Honey Lathering").And.Contain("26x ENCHANTMENT_ULTIMATE_BOBBIN_TIME_3").And.Contain("p1");
        logger.Lines[2].Should().Contain("2 periods").And.Contain("3 items");
    }
}
