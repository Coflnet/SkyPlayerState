using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── Base Mining Task ──
public abstract class BaseMiningTask : MethodTask
{
    public override List<StatFactor> StatFactors =>
    [
        new("hotm:tier", 0.35, 10),
        new("skill:Mining", 0.30, 60),
        new("gear:GAUNTLET", 0.25, 6),
        new("attr:Mining Speed", 0.10, 10)
    ];
    protected override string Category => "Mining";
    protected override string ActionUnit => "mines";
    protected override List<RequiredItem> RequiredItems => [new() { ItemTag = "GEMSTONE_GAUNTLET", Reason = "Mining tool" }];
    protected override List<DropEffect> Effects => [
        new() { Name = "Mining Speed", Description = "Faster block breaking", EstimatedMultiplier = 1.3 },
        new() { Name = "Mining Fortune", Description = "More drops per block", EstimatedMultiplier = 1.5 },
        new() { Name = "Pristine", Description = "Chance to upgrade gem quality on mine", EstimatedMultiplier = 1.2 }
    ];

    /// <summary>
    /// Shared simple-words step list for the gemstone mining tasks below (all verified 200 on
    /// the community wiki 2026-09). <paramref name="accessNote"/> covers the one thing that
    /// differs between them: Crystal Hollows needs HotM 3/4 + a Crystal Hollows Pass, the Glacite
    /// area (Peridot/Jasper via Glacite Tunnels) just needs the Dwarven Mines unlocked.
    /// </summary>
    protected static List<TaskStep> GemstoneSteps(string gemName, string wikiUrl, string accessNote)
    {
        var steps = new List<TaskStep>();
        void Add(string text, string onClick = null) => steps.Add(new TaskStep { Number = steps.Count + 1, Text = text, OnClick = onClick });

        Add("Get a Gemstone Gauntlet - it's the only tool that can mine gemstones.",
            "https://hypixelskyblock.minecraft.wiki/w/Gemstone_Gauntlet");
        Add(accessNote);
        Add($"Look for glowing {gemName} veins in the walls and mine them with your Gemstone Gauntlet.", wikiUrl);
        Add($"You're doing it right when you collect: Rough, Flawed, and Fine {gemName} Gemstones. That's what we track for your coins/hour.");
        Add($"Sell your {gemName} gemstones on the Bazaar.");
        Add("Check /cofl task again to see your real coins/hour.");
        return steps;
    }

    protected const string CrystalHollowsAccessNote =
        "Type /warp crystals to get close, then go into the Crystal Hollows - you need Heart of the "
        + "Mountain level 3 or 4 and a Crystal Hollows Pass to get in.";
    protected const string GlaciteAccessNote =
        "Type /warp mines to get close, then go down into the Glacite Tunnels in the Dwarven Mines.";
}

// ── Gemstone Mining ──
public class ThystMiningTask : BaseMiningTask
{
    protected override string MethodName => "Thyst Mining";
    protected override HashSet<string> Locations => ["Crystal Hollows", "Mithril Deposits", "Precursor Remnants", "Magma Fields", "Goblin Holdout"];
    protected override HashSet<string> DetectionItems => ["FINE_AMETHYST_GEM", "FLAWED_AMETHYST_GEM", "ROUGH_AMETHYST_GEM"];
    protected override List<MethodDrop> FormulaDrops => [new("FINE_AMETHYST_GEM", 200), new("FLAWED_AMETHYST_GEM", 400)];
    protected override string HowTo => "Equip a Gemstone Gauntlet and mine Amethyst (Thyst) gemstone veins in the Crystal Hollows.";
    protected override double ActionsPerHour => 3200;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Amethyst_Gemstone";
    protected override List<TaskStep> Steps => GemstoneSteps("Amethyst (Thyst)", WikiUrl, CrystalHollowsAccessNote);
}
public class JasperMiningTask : BaseMiningTask
{
    protected override string MethodName => "Jasper Mining";
    protected override HashSet<string> Locations => ["Crystal Hollows", "Fairy Grotto", "Glacite Mineshafts"];
    protected override HashSet<string> DetectionItems => ["FINE_JASPER_GEM", "FLAWED_JASPER_GEM", "ROUGH_JASPER_GEM"];
    protected override List<MethodDrop> FormulaDrops => [new("FINE_JASPER_GEM", 180), new("FLAWED_JASPER_GEM", 360)];
    protected override string HowTo => "Equip a Gemstone Gauntlet and mine Jasper gemstone veins in the Crystal Hollows.";
    protected override double ActionsPerHour => 3000;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Jasper_Gemstone";
    protected override List<TaskStep> Steps => GemstoneSteps("Jasper", WikiUrl, CrystalHollowsAccessNote);
}
public class JadeMiningTask : BaseMiningTask
{
    protected override string MethodName => "Jade Mining";
    protected override HashSet<string> Locations => ["Crystal Hollows", "Mithril Deposits", "Goblin Holdout"];
    protected override HashSet<string> DetectionItems => ["FINE_JADE_GEM", "FLAWED_JADE_GEM", "ROUGH_JADE_GEM"];
    protected override List<MethodDrop> FormulaDrops => [new("FINE_JADE_GEM", 170), new("FLAWED_JADE_GEM", 340)];
    protected override string HowTo => "Equip a Gemstone Gauntlet and mine Jade gemstone veins in the Crystal Hollows.";
    protected override double ActionsPerHour => 2900;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Jade_Gemstone";
    protected override List<TaskStep> Steps => GemstoneSteps("Jade", WikiUrl, CrystalHollowsAccessNote);
}
public class AmberMiningTask : BaseMiningTask
{
    protected override string MethodName => "Amber Mining";
    protected override HashSet<string> Locations => ["Crystal Hollows", "Goblin Holdout"];
    protected override HashSet<string> DetectionItems => ["FINE_AMBER_GEM", "FLAWED_AMBER_GEM", "ROUGH_AMBER_GEM"];
    protected override List<MethodDrop> FormulaDrops => [new("FINE_AMBER_GEM", 170), new("FLAWED_AMBER_GEM", 340)];
    protected override string HowTo => "Equip a Gemstone Gauntlet and mine Amber gemstone veins in the Crystal Hollows.";
    protected override double ActionsPerHour => 2900;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Amber_Gemstone";
    protected override List<TaskStep> Steps => GemstoneSteps("Amber", WikiUrl, CrystalHollowsAccessNote);
}
public class SapphireMiningTask : BaseMiningTask
{
    protected override string MethodName => "Sapphire Mining";
    protected override HashSet<string> Locations => ["Crystal Hollows", "Precursor Remnants"];
    protected override HashSet<string> DetectionItems => ["FINE_SAPPHIRE_GEM", "FLAWED_SAPPHIRE_GEM", "ROUGH_SAPPHIRE_GEM"];
    protected override List<MethodDrop> FormulaDrops => [new("FINE_SAPPHIRE_GEM", 160), new("FLAWED_SAPPHIRE_GEM", 320)];
    protected override string HowTo => "Equip a Gemstone Gauntlet and mine Sapphire gemstone veins in the Crystal Hollows.";
    protected override double ActionsPerHour => 2800;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Sapphire_Gemstone";
    protected override List<TaskStep> Steps => GemstoneSteps("Sapphire", WikiUrl, CrystalHollowsAccessNote);
}
public class PeridotMiningTask : BaseMiningTask
{
    protected override string MethodName => "Peridot Mining";
    protected override HashSet<string> Locations => ["Glacite Tunnels", "Glacite Mineshafts"];
    protected override HashSet<string> DetectionItems => ["FINE_PERIDOT_GEM", "FLAWED_PERIDOT_GEM", "ROUGH_PERIDOT_GEM"];
    protected override List<MethodDrop> FormulaDrops => [new("FINE_PERIDOT_GEM", 160), new("FLAWED_PERIDOT_GEM", 320)];
    protected override string HowTo => "Equip a Gemstone Gauntlet and mine Peridot gemstone veins in the Crystal Hollows.";
    protected override double ActionsPerHour => 2800;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Peridot_Gemstone";
    protected override List<TaskStep> Steps => GemstoneSteps("Peridot", WikiUrl, GlaciteAccessNote);
}

// ── Ore Mining ──
public class CoalMiningTask : BaseMiningTask
{
    protected override string MethodName => "Coal Mining";
    protected override HashSet<string> Locations => ["Dwarven Mines", "Coal Mine", "Crystal Hollows", "Gold Mine"];
    protected override HashSet<string> DetectionItems => ["COAL", "ENCHANTED_COAL"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_COAL", 500), new("ENCHANTED_COAL_BLOCK", 30)];
    protected override string HowTo => "Mine coal ore blocks in the Dwarven Mines or Glacite Tunnels with a pickaxe.";
    protected override double ActionsPerHour => 4000;
}
public class DiamondMiningTask : BaseMiningTask
{
    protected override string MethodName => "Diamond Mining";
    protected override HashSet<string> Locations => ["Dwarven Mines", "Deep Caverns", "Diamond Reserve"];
    protected override HashSet<string> DetectionItems => ["DIAMOND", "ENCHANTED_DIAMOND"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_DIAMOND", 300), new("ENCHANTED_DIAMOND_BLOCK", 15)];
    protected override string HowTo => "Mine diamond ore blocks in the Deep Caverns or Diamond Reserve with a pickaxe.";
    protected override double ActionsPerHour => 3500;
}
public class RedstoneMiningTask : BaseMiningTask
{
    protected override string MethodName => "Redstone Mining";
    protected override HashSet<string> Locations => ["Dwarven Mines", "Deep Caverns", "Pigmen's Den"];
    protected override HashSet<string> DetectionItems => ["REDSTONE", "ENCHANTED_REDSTONE"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_REDSTONE", 400), new("ENCHANTED_REDSTONE_BLOCK", 20)];
    protected override string HowTo => "Mine redstone ore blocks in the Deep Caverns or Pigmen's Den with a pickaxe.";
    protected override double ActionsPerHour => 3800;
}
public class CobblestoneMiningTask : BaseMiningTask
{
    protected override string MethodName => "Cobblestone Mining";
    protected override HashSet<string> Locations => ["Gold Mine", "Deep Caverns", "Coal Mine"];
    protected override HashSet<string> DetectionItems => ["COBBLESTONE", "ENCHANTED_COBBLESTONE"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_COBBLESTONE", 600)];
    protected override string HowTo => "Mine cobblestone in the Gold Mine or Coal Mine with a pickaxe.";
    protected override double ActionsPerHour => 5000;
}
public class ObsidianMiningTask : BaseMiningTask
{
    protected override string MethodName => "Obsidian Mining";
    protected override HashSet<string> Locations => ["Obsidian Sanctuary", "Deep Caverns", "The End"];
    protected override HashSet<string> DetectionItems => ["OBSIDIAN", "ENCHANTED_OBSIDIAN"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_OBSIDIAN", 200)];
    protected override string HowTo => "Mine obsidian blocks in the Obsidian Sanctuary or The End with a pickaxe.";
    protected override double ActionsPerHour => 2000;
}
public class TungstenMiningTask : BaseMiningTask
{
    protected override string MethodName => "Tungsten Mining";
    protected override HashSet<string> Locations => ["Glacite Tunnels", "Glacite Mineshafts"];
    protected override HashSet<string> DetectionItems => ["TUNGSTEN", "ENCHANTED_TUNGSTEN"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_TUNGSTEN", 200)];
    protected override string HowTo => "Mine tungsten ore in the Glacite Tunnels or Glacite Mountains with a pickaxe.";
    protected override double ActionsPerHour => 2500;
}
public class UmberMiningTask : BaseMiningTask
{
    protected override string MethodName => "Umber Mining";
    protected override HashSet<string> Locations => ["Glacite Tunnels", "Glacite Mineshafts"];
    protected override HashSet<string> DetectionItems => ["UMBER", "ENCHANTED_UMBER"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_UMBER", 200)];
    protected override string HowTo => "Mine umber ore in the Glacite Tunnels or Glacite Mountains with a pickaxe.";
    protected override double ActionsPerHour => 2500;
}

// ── Special Mining ──
public class NucleusMiningTask : BaseMiningTask
{
    protected override string MethodName => "Crystal Nucleus";
    protected override HashSet<string> Locations => ["Crystal Nucleus", "Crystal Hollows"];
    // Nucleus treasure stash yields Divan Fragments (for Divan's Alloy) + fine gemstones.
    protected override HashSet<string> DetectionItems => ["DIVAN_FRAGMENT"];
    protected override List<MethodDrop> FormulaDrops => [new("DIVAN_FRAGMENT", 0.3)];
    protected override string HowTo => "Collect crystal fragments and complete Nucleus runs in the Crystal Hollows.";
    protected override double ActionsPerHour => 600;
}
public class SludgeMiningTask : BaseMiningTask
{
    protected override string MethodName => "Sludge Mining";
    // Sludge Juice (~3% chance) drops from mining Hard Stone (or killing Sludge mobs) in the
    // Jungle sub-zone of the Crystal Hollows with a Jungle Pickaxe - not a Crystal-Hollows-wide
    // drop, and not the Dwarven Mines. See https://hypixelskyblock.minecraft.wiki/w/Sludge_Juice.
    protected override HashSet<string> Locations => ["Jungle", "Jungle Temple"];
    protected override HashSet<string> DetectionItems => ["SLUDGE_JUICE"];
    protected override List<MethodDrop> FormulaDrops => [new("SLUDGE_JUICE", 2000), new("HARD_STONE", 500)];
    protected override List<RequiredItem> RequiredItems =>
        [new() { ItemTag = "JUNGLE_PICKAXE", Reason = "Only pickaxe with a chance to drop Sludge Juice while mining Hard Stone in the Jungle" }];
    protected override string HowTo =>
        "Mine Hard Stone (or kill Sludge mobs) in the Jungle sub-zone of the Crystal Hollows with a "
        + "Jungle Pickaxe (bought from Odawa for 200 Sludge Juice) for a chance at Sludge Juice.";
    protected override double ActionsPerHour => 3000;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Sludge_Juice";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Get into the Crystal Hollows: you need Heart of the Mountain level 3 or 4, and a Crystal Hollows Pass." },
        new() { Number = 2, Text = "Type /warp crystals to get close.", OnClick = "/warp crystals" },
        new() { Number = 3, Text = "Walk to the Jungle area inside the Crystal Hollows.", OnClick = WhereWikiUrl },
        new() { Number = 4, Text = "The first time, buy a Jungle Pickaxe from Odawa (an NPC standing in the Jungle) for 200 Sludge Juice - it's the only pickaxe that can find Sludge Juice.", OnClick = "https://hypixelskyblock.minecraft.wiki/w/Jungle_Pickaxe" },
        new() { Number = 5, Text = "Mine the grey Hard Stone blocks with your Jungle Pickaxe. Sometimes one turns into Sludge and drops Sludge Juice." },
        new() { Number = 6, Text = "You're doing it right when you collect: Sludge Juice. That's what we track for your coins/hour.", OnClick = WikiUrl },
        new() { Number = 7, Text = "Sell Sludge Juice on the Bazaar." },
        new() { Number = 8, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
public class SludgeMiningGemMixtureTask : BaseMiningTask
{
    protected override string MethodName => "Sludge Mining (Gem Mixture)";
    protected override HashSet<string> Locations => ["Jungle", "Jungle Temple"];
    protected override HashSet<string> DetectionItems => ["SLUDGE_JUICE", "GEMSTONE_MIXTURE"];
    // Gemstone Mixture is NOT a drop - it is a Forge recipe (320 Sludge Juice + 4 Fine Jade +
    // 4 Fine Amber + 4 Fine Amethyst + 4 Fine Sapphire, 4h forge time). Model the conversion
    // honestly instead of double counting raw juice AND mixtures: assume every mined Sludge
    // Juice is fed into the forge, net the 16 fine gems it costs per mixture via FormulaCosts.
    // The Forge itself - not gathering speed - is almost always the bottleneck: assuming 5 Forge
    // slots at 4h/craft caps real throughput at 5/4 = 1.25 mixtures/h, far below the raw
    // juice/320 conversion rate, so the cap (not the gathering rate) drives the estimate. Juice
    // beyond what the capped forge rate consumes is not wasted - it is valued as a raw
    // SLUDGE_JUICE drop instead (sold directly rather than fed into a queued Forge slot).
    private const double SludgeJuicePerHour = 2000;
    private const double SludgeJuicePerMixture = 320;
    private const double AssumedForgeSlots = 5;
    private const double ForgeHoursPerMixture = 4;
    /// <summary>Real Forge throughput assuming 5 concurrent slots at 4h/craft each.</summary>
    private const double ForgeCapPerHour = AssumedForgeSlots / ForgeHoursPerMixture; // 1.25/h
    private const double UncappedMixtureRate = SludgeJuicePerHour / SludgeJuicePerMixture; // 6.25/h
    private const double MixtureRate = UncappedMixtureRate > ForgeCapPerHour ? ForgeCapPerHour : UncappedMixtureRate;
    private const double JuiceConsumedByForge = MixtureRate * SludgeJuicePerMixture;
    private const double LeftoverJuicePerHour = SludgeJuicePerHour - JuiceConsumedByForge;
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("GEMSTONE_MIXTURE", MixtureRate), new("HARD_STONE", 500),
        new("SLUDGE_JUICE", LeftoverJuicePerHour)
    ];
    protected override List<MethodDrop> FormulaCosts =>
    [
        new("FINE_JADE_GEM", MixtureRate * 4), new("FINE_AMBER_GEM", MixtureRate * 4),
        new("FINE_AMETHYST_GEM", MixtureRate * 4), new("FINE_SAPPHIRE_GEM", MixtureRate * 4)
    ];
    protected override List<RequiredItem> RequiredItems =>
        [new() { ItemTag = "JUNGLE_PICKAXE", Reason = "Only pickaxe with a chance to drop Sludge Juice while mining Hard Stone in the Jungle" }];
    protected override string HowTo =>
        "Mine Sludge Juice in the Jungle (Crystal Hollows) with a Jungle Pickaxe, then Forge it into "
        + "Gemstone Mixture (320 Sludge Juice + 4 Fine Jade/Amber/Amethyst/Sapphire gems each, 4h per craft). "
        + "Assuming 5 Forge slots running at once, the Forge caps real throughput at 5 crafts / 4h = 1.25 "
        + "mixtures/h - far below how fast Sludge Juice is gathered, so the Forge (not mining speed) is the "
        + "bottleneck. Sell the juice you can't queue into the Forge yet as raw Sludge Juice instead.";
    protected override double ActionsPerHour => 3000;
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Gemstone_Mixture";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Get into the Crystal Hollows: you need Heart of the Mountain level 3 or 4, and a Crystal Hollows Pass." },
        new() { Number = 2, Text = "Type /warp crystals to get close.", OnClick = "/warp crystals" },
        new() { Number = 3, Text = "Walk to the Jungle area inside the Crystal Hollows and buy a Jungle Pickaxe from Odawa (200 Sludge Juice, one time only).", OnClick = "https://hypixelskyblock.minecraft.wiki/w/Jungle_Pickaxe" },
        new() { Number = 4, Text = "Mine Hard Stone blocks with your Jungle Pickaxe to collect Sludge Juice. This step is exactly the same as Sludge Mining, so it counts for both." },
        new() { Number = 5, Text = "When you have a lot of Sludge Juice, type /warp forge to go to the Forge in the Dwarven Mines.", OnClick = "/warp forge" },
        new() { Number = 6, Text = "Craft Gemstone Mixture: 320 Sludge Juice + 4 Fine Jade + 4 Fine Amber + 4 Fine Amethyst + 4 Fine Sapphire Gemstones. It takes 4 hours to finish.", OnClick = WikiUrl },
        new() { Number = 7, Text = "A Forge only has a few slots, so you can only finish about 1 Gemstone Mixture every 4 hours in total - it's normal to also sell your extra Sludge Juice directly instead of waiting for a free slot." },
        new() { Number = 8, Text = "Sell your Gemstone Mixture and any leftover Sludge Juice on the Bazaar or Auction House." },
        new() { Number = 9, Text = "Check /cofl task again to see your real coins/hour." },
    ];
    // Shares "Sludge Mining"'s detection: Gemstone Mixture only ever comes out of the Forge (at
    // /warp forge, Dwarven Mines), never the Jungle where the juice is mined, so requiring this
    // task's own DetectionItems to show up there would mean it (and its "doing this now" doers)
    // never gets detected - see TaskClassifier.GetDerivedTaskNames / TaskPeriodFolder.FoldDerivedTasks.
    protected override string DerivedFrom => "Sludge Mining";
    protected override Dictionary<string, double> ConvertDerivedCounts(Dictionary<string, double> primaryItemCounts, double hours)
    {
        if (hours <= 0)
            return null;
        var juice = primaryItemCounts.GetValueOrDefault("SLUDGE_JUICE");
        if (juice <= 0)
            return null;
        var juicePerHour = juice / hours;
        var mixturesPerHour = Math.Min(juicePerHour / SludgeJuicePerMixture, ForgeCapPerHour);
        if (mixturesPerHour <= 0)
            return null;
        var leftoverJuicePerHour = Math.Max(0, juicePerHour - mixturesPerHour * SludgeJuicePerMixture);
        return new Dictionary<string, double>
        {
            ["GEMSTONE_MIXTURE"] = mixturesPerHour * hours,
            ["SLUDGE_JUICE"] = leftoverJuicePerHour * hours
        };
    }
}
// Kept as a class only (never registered, see TaskCatalog.IntentionallyUnregistered): there is no
// known "sludge -> coal" drop variant. Coal ore does not spawn in the Jungle sub-zone where Sludge
// Juice is mined, so this task's premise (mining sludge yields bonus Coal) is fabricated.
public class SludgeMiningCoalTask : BaseMiningTask
{
    protected override string MethodName => "Sludge Mining (Coal)";
    protected override HashSet<string> Locations => ["Jungle", "Jungle Temple"];
    protected override HashSet<string> DetectionItems => ["SLUDGE_JUICE", "ENCHANTED_COAL"];
    protected override List<MethodDrop> FormulaDrops => [new("SLUDGE_JUICE", 2000), new("ENCHANTED_COAL", 200)];
    protected override string HowTo => "Not a real method - Coal does not spawn in the Jungle sub-zone sludge is mined in.";
    protected override double ActionsPerHour => 3000;
}
public class ScathaMiningTask : BaseMiningTask
{
    protected override string MethodName => "Scatha Mining";
    // Scatha/Worm spawn only in the Crystal Hollows; they drop gemstones, not worm membrane.
    protected override HashSet<string> Locations => ["Crystal Hollows"];
    protected override HashSet<string> DetectionItems => ["PET_SCATHA"];
    protected override List<MethodDrop> FormulaDrops => [new("FINE_TOPAZ_GEM", 15), new("FINE_AMETHYST_GEM", 6), new("ROUGH_TOPAZ_GEM", 50), new("FINE_JADE_GEM", 3)];
    protected override string HowTo => "Mine in the Crystal Hollows to spawn and kill Scatha worms for rare drops.";
    protected override double ActionsPerHour => 1500;
}

// ── Powder Mining ──
public class PrecursorCityPowderMiningTask : BaseMiningTask
{
    protected override string MethodName => "Precursor City Powder Mining";
    protected override HashSet<string> Locations => ["Precursor Remnants", "Lost Precursor City"];
    protected override HashSet<string> DetectionItems => ["MITHRIL_ORE", "ENCHANTED_MITHRIL", "ROUGH_SAPPHIRE_GEM"];
    // The profit driver here is Sapphire gemstones + Automaton robot parts, not just mithril.
    protected override List<MethodDrop> FormulaDrops =>
    [
        new("ROUGH_SAPPHIRE_GEM", 2000), new("SYNTHETIC_HEART", 0.5), new("CONTROL_SWITCH", 0.5),
        new("SUPERLITE_MOTOR", 0.5), new("ROBOTRON_REFLECTOR", 0.5), new("FTX_3070", 0.5),
        new("ELECTRON_TRANSMITTER", 0.5), new("ENCHANTED_MITHRIL", 200)
    ];
    protected override string HowTo => "Mine mithril and hardstone in the Precursor City area for powder and mithril drops.";
    protected override double ActionsPerHour => 3500;
}
public class JunglePowderMiningTask : BaseMiningTask
{
    protected override string MethodName => "Jungle Powder Mining";
    protected override HashSet<string> Locations => ["Jungle", "Jungle Temple"];
    protected override HashSet<string> DetectionItems => ["MITHRIL_ORE", "ENCHANTED_MITHRIL", "HARD_STONE"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_MITHRIL", 180), new("HARD_STONE", 500)];
    protected override string HowTo => "Mine mithril and hardstone in the Jungle area for powder and mithril drops.";
    protected override double ActionsPerHour => 3200;
}
public class MithrilDepositsPowderMiningTask : BaseMiningTask
{
    protected override string MethodName => "Mithril Deposits Powder Mining";
    // Mithril Deposits is a Crystal Hollows sub-zone, not part of the Dwarven Mines.
    protected override HashSet<string> Locations => ["Crystal Hollows", "Mithril Deposits"];
    protected override HashSet<string> DetectionItems => ["MITHRIL_ORE", "ENCHANTED_MITHRIL"];
    protected override List<MethodDrop> FormulaDrops => [new("ENCHANTED_MITHRIL", 200)];
    protected override string HowTo => "Mine mithril ore in the Mithril Deposits (Crystal Hollows) for powder and mithril drops.";
    protected override double ActionsPerHour => 3500;
}
public class GoblinHoldoutPowderMiningTask : BaseMiningTask
{
    protected override string MethodName => "Goblin Holdout Powder Mining";
    protected override HashSet<string> Locations => ["Goblin Holdout", "Goblin Queen's Den"];
    protected override HashSet<string> DetectionItems => ["MITHRIL_ORE", "ENCHANTED_MITHRIL"];
    // Treasure chests spawned while mining hardstone here drop Goblin Eggs (the real profit driver).
    protected override List<MethodDrop> FormulaDrops => [new("GOBLIN_EGG", 200), new("ENCHANTED_MITHRIL", 180)];
    protected override string HowTo => "Mine mithril ore in the Goblin Holdout for powder and mithril drops.";
    protected override double ActionsPerHour => 3200;
}
