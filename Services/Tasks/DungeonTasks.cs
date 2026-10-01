using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── Base class for dungeon tasks ──
public abstract class BaseDungeonTask : MethodTask
{
    protected override string Category => "Dungeon";
    protected override string ActionUnit => "runs";
    // A Catacombs floor zone ("The Catacombs (F6)", "(M7)", ...) is a dedicated dungeon instance -
    // it can only ever mean that one floor, so a handful of reward-chest items (SUPERBOOM_TNT,
    // ENCHANTED_BONE, ...) is already unambiguous evidence, unlike a shared open-world zone where 5
    // generic items are needed to rule out incidental activity. See TaskPlausibility/TaskClassifier
    // regression for "The Catacombs (F6)" with only 2 items still classifying to F6.
    protected override int MinLocationOnlyItems => 1;
    // Chest cost / run counter are supporting evidence for the floor (a period holding only
    // "-6000000x DUNGEON_CHEST_COST" is that floor, and its cost must not lose to e.g. a /bz
    // purchase bucket) but are not mandatory - see MethodTask.EvidenceItems.
    protected override HashSet<string> EvidenceItems => [PseudoItems.DUNGEON_CHEST_COST, PseudoItems.DUNGEON_RUN];
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Dungeon Class Level", Description = "Higher class level increases damage and survivability", EstimatedMultiplier = 1.2 },
        new() { Name = "Catacombs Level", Description = "Higher catacombs level unlocks better drops", EstimatedMultiplier = 1.3 },
        new() { Name = "Kismet Feather", Description = "Rerolls dungeon chest drops for better loot", EstimatedMultiplier = 1.5 }
    ];
}

/// <summary>
/// Shared metadata/step-building for the individual Catacombs floor tasks (F1-F7 Normal Mode,
/// M1-M3 Master Mode - M4-M7 stay their own hand-written classes below, unchanged, since they
/// predate this base). Keeps every concrete floor class down to a handful of property overrides
/// (floor numeral, boss, level requirement, FormulaDrops) while every task still carries accurate,
/// floor-specific HowTo/Steps text - see <see cref="DungeonRewardAttribution"/> for how a reward
/// chest claimed later in the Dungeon Hub gets folded back into the run's floor period, and
/// <see cref="Services.DungeonRewardListener"/> for the chest-opening coin cost these steps mention.
/// <para>
/// Catacombs level requirements verified 2026-09 against
/// https://hypixelskyblock.minecraft.wiki/w/Catacombs: Normal Mode column F1..F7 = 1, 3, 5, 9, 14,
/// 19, 24; Master Mode column (= M1..M7's own requirement) = 24, 26, 28, 30, 32, 34, 36. Boss names
/// verified on the same page and cross-checked against the per-floor pages (e.g.
/// https://hypixelskyblock.minecraft.wiki/w/The_Catacombs_-_Floor_I, which also confirmed the page
/// covers both the Normal and Master Mode variant of that floor number, so M1-M3 share the F1-F3
/// WikiUrl - all seven Floor_I..Floor_VII pages return HTTP 200).
/// </para>
/// </summary>
public abstract class BaseCatacombsFloorTask : BaseDungeonTask
{
    /// <summary>Roman-numeral floor number this task covers (I-VII) - shared between e.g. F3 and M3.</summary>
    protected abstract string FloorNumeral { get; }
    /// <summary>True for the M1-M3 Master Mode variant of this floor.</summary>
    protected abstract bool IsMaster { get; }
    protected abstract string BossName { get; }
    protected abstract int RequiredCatacombsLevel { get; }

    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/The_Catacombs_-_Floor_" + FloorNumeral;
    private string ModeLabel => IsMaster ? "Master Mode " : "";

    protected override string HowTo =>
        $"Queue for {ModeLabel}Floor {FloorNumeral} in the Dungeon Hub. Requires Catacombs level {RequiredCatacombsLevel}+. "
        + $"Boss fight is {BossName}. Open the reward chests as you go, or claim them later at Croesus in the Dungeon "
        + "Hub; opening costs coins, which are subtracted from your profit.";

    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Type /warp dungeon_hub to get close.", OnClick = "/warp dungeon_hub" },
        new() { Number = 2, Text = $"Queue for {ModeLabel}Floor {FloorNumeral} (needs Catacombs level {RequiredCatacombsLevel}+), with a good party of 5.", OnClick = WhereWikiUrl },
        new() { Number = 3, Text = $"Clear the dungeon and fight the boss, {BossName}." },
        new() { Number = 4, Text = "Open the reward chests (or claim them later at Croesus in the Dungeon Hub); opening costs coins, which are subtracted from your profit.", OnClick = WikiUrl },
        new() { Number = 5, Text = $"You're doing it right when you start collecting: {string.Join(", ", FormulaDrops.Select(d => d.ItemTag))}." },
        new() { Number = 6, Text = "Sell the essence on the Bazaar/Auction House." },
        new() { Number = 7, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}

// ── Normal Mode floors (F1-F7) ──
// FormulaDrops seeded from the per-floor essence loot tables verified on the Floor_I/Floor_IV/
// Floor_VII wiki pages (2026-09) times a conservative, floor-appropriate runs/hour (fast early
// floors clear in a few minutes; F7 needs a full boss gauntlet) - kept well below the existing
// M4-M7 rates (300-600/h) since normal-mode chests give noticeably less essence per run than
// Master Mode ones. ESSENCE_UNDEAD for F1-F4, ESSENCE_WITHER for F5-F7 and every Master floor,
// matching what those floors' reward chests actually contain.
public class F1Task : BaseCatacombsFloorTask
{
    protected override string MethodName => "F1";
    protected override HashSet<string> Locations => ["The Catacombs (F1)"];
    protected override string FloorNumeral => "I";
    protected override bool IsMaster => false;
    protected override string BossName => "Bonzo";
    protected override int RequiredCatacombsLevel => 1;
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_UNDEAD", 80)];
}
public class F2Task : BaseCatacombsFloorTask
{
    protected override string MethodName => "F2";
    protected override HashSet<string> Locations => ["The Catacombs (F2)"];
    protected override string FloorNumeral => "II";
    protected override bool IsMaster => false;
    protected override string BossName => "Scarf";
    protected override int RequiredCatacombsLevel => 3;
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_UNDEAD", 100)];
}
public class F3Task : BaseCatacombsFloorTask
{
    protected override string MethodName => "F3";
    protected override HashSet<string> Locations => ["The Catacombs (F3)"];
    protected override string FloorNumeral => "III";
    protected override bool IsMaster => false;
    protected override string BossName => "The Professor";
    protected override int RequiredCatacombsLevel => 5;
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_UNDEAD", 120)];
}
public class F4Task : BaseCatacombsFloorTask
{
    protected override string MethodName => "F4";
    protected override HashSet<string> Locations => ["The Catacombs (F4)"];
    protected override string FloorNumeral => "IV";
    protected override bool IsMaster => false;
    protected override string BossName => "Thorn";
    protected override int RequiredCatacombsLevel => 9;
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_UNDEAD", 150)];
}
public class F5Task : BaseCatacombsFloorTask
{
    protected override string MethodName => "F5";
    protected override HashSet<string> Locations => ["The Catacombs (F5)"];
    protected override string FloorNumeral => "V";
    protected override bool IsMaster => false;
    protected override string BossName => "Livid";
    protected override int RequiredCatacombsLevel => 14;
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 170)];
}
public class F6Task : BaseCatacombsFloorTask
{
    protected override string MethodName => "F6";
    protected override HashSet<string> Locations => ["The Catacombs (F6)"];
    protected override string FloorNumeral => "VI";
    protected override bool IsMaster => false;
    protected override string BossName => "Sadan";
    protected override int RequiredCatacombsLevel => 19;
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 190)];
}
public class F7Task : BaseCatacombsFloorTask
{
    protected override string MethodName => "F7";
    protected override HashSet<string> Locations => ["The Catacombs (F7)"];
    protected override string FloorNumeral => "VII";
    protected override bool IsMaster => false;
    protected override string BossName => "Maxor, Storm, Goldor and Necron";
    protected override int RequiredCatacombsLevel => 24;
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 210)];
}

// ── Master Mode floors I-III (M1-M3) ──
// M4-M7 already existed before this change (see below) at 300/400/500/600 ESSENCE_WITHER/h; M1-M3
// lead into that same progression from below (230/260/280) rather than restarting it, since M1 is
// less demanding than M4 (fewer required secrets/phases) despite sharing the Catacombs level 24
// entry requirement with F7.
public class M1Task : BaseCatacombsFloorTask
{
    protected override string MethodName => "M1";
    protected override HashSet<string> Locations => ["The Catacombs (M1)"];
    protected override string FloorNumeral => "I";
    protected override bool IsMaster => true;
    protected override string BossName => "Master Bonzo";
    protected override int RequiredCatacombsLevel => 24;
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 230)];
}
public class M2Task : BaseCatacombsFloorTask
{
    protected override string MethodName => "M2";
    protected override HashSet<string> Locations => ["The Catacombs (M2)"];
    protected override string FloorNumeral => "II";
    protected override bool IsMaster => true;
    protected override string BossName => "Master Scarf";
    protected override int RequiredCatacombsLevel => 26;
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 260)];
}
public class M3Task : BaseCatacombsFloorTask
{
    protected override string MethodName => "M3";
    protected override HashSet<string> Locations => ["The Catacombs (M3)"];
    protected override string FloorNumeral => "III";
    protected override bool IsMaster => true;
    protected override string BossName => "Master Professor";
    protected override int RequiredCatacombsLevel => 28;
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 280)];
}

public class M4Task : BaseDungeonTask
{
    protected override string MethodName => "M4";
    // Bare "The Catacombs"/"Master Mode Catacombs Floor IV" deliberately removed: neither ever
    // occurs on the real scoreboard (verified against production logs), and the bare island-level
    // name matched EVERY floor's periods via SkyblockZones.Matches' island fallback, with the
    // alphabetical tie-break always picking M4 - see SkyblockZones.cs for the real per-floor zone
    // strings ("The Catacombs (M4)" etc.) this now matches instead.
    protected override HashSet<string> Locations => ["The Catacombs (M4)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 300)];
    protected override string HowTo => "Queue for Master Mode Floor 4 in the Dungeon Hub. Requires Catacombs level 30+. Run with a party of 5 for efficient clears.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "WITHER_CHESTPLATE", Reason = "Dungeon armor for survivability" }
    ];
}
public class M5Task : BaseDungeonTask
{
    protected override string MethodName => "M5";
    protected override HashSet<string> Locations => ["The Catacombs (M5)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 400)];
    protected override string HowTo => "Queue for Master Mode Floor 5. Requires Catacombs level 32+. Boss fight is Professor with Guardians phase.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "WITHER_CHESTPLATE", Reason = "Dungeon armor for survivability" }
    ];
}
public class M6Task : BaseDungeonTask
{
    protected override string MethodName => "M6";
    protected override HashSet<string> Locations => ["The Catacombs (M6)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 500)];
    protected override string HowTo => "Queue for Master Mode Floor 6. Requires Catacombs level 34+. Boss is Sadan with terracotta phases.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "WITHER_CHESTPLATE", Reason = "Dungeon armor for survivability" },
        new() { ItemTag = "HYPERION", Reason = "Mage weapon for efficient clears" }
    ];
}
public class M7Task : BaseDungeonTask
{
    protected override string MethodName => "M7";
    protected override HashSet<string> Locations => ["The Catacombs (M7)"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 600), new("NECRON_HANDLE", 0.05)];
    protected override string HowTo => "Queue for Master Mode Floor 7. Requires Catacombs level 36+. Boss is Necron with multiple phases. Handle drop is rare (~1/20).";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "HYPERION", Reason = "Best mage weapon for M7" },
        new() { ItemTag = "TERMINATOR", Reason = "Best archer weapon for M7" }
    ];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Necron";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Get your gear ready: a Hyperion (mage) or Terminator (archer) is best for M7." },
        new() { Number = 2, Text = "Type /warp dungeon_hub to get close.", OnClick = "/warp dungeon_hub" },
        new() { Number = 3, Text = "Queue for Master Mode Floor 7 (needs Catacombs level 36+), with a good party of 5.", OnClick = WhereWikiUrl },
        new() { Number = 4, Text = "Clear the dungeon and fight the boss, Necron - he has several phases." },
        new() { Number = 5, Text = "You're doing it right when you collect: Wither Essence, and rarely a Necron's Handle (about 1 in 20 runs).", OnClick = WikiUrl },
        new() { Number = 6, Text = "Sell the Wither Essence, and the Handle if you get one, on the Bazaar/Auction House." },
        new() { Number = 7, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
public class M7KismetTask : BaseDungeonTask
{
    protected override string MethodName => "M7 (Kismet)";
    protected override HashSet<string> Locations => ["The Catacombs (M7)"];
    protected override HashSet<string> DetectionItems => ["KISMET_FEATHER"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 600), new("NECRON_HANDLE", 0.1)];
    protected override List<MethodDrop> FormulaCosts => [new("KISMET_FEATHER", 1)];
    protected override string HowTo => "Queue for Master Mode Floor 7 with Kismet Feathers for double chest reroll. Doubles the handle chance but costs a Kismet per run.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "KISMET_FEATHER", Reason = "Rerolls dungeon chest for better drops" },
        new() { ItemTag = "HYPERION", Reason = "Best mage weapon for M7" }
    ];
    protected override string WikiUrl => "https://hypixelskyblock.minecraft.wiki/w/Necron";
    protected override List<TaskStep> Steps =>
    [
        new() { Number = 1, Text = "Get your gear ready: a Hyperion (mage) or Terminator (archer) is best for M7.", OnClick = "https://hypixelskyblock.minecraft.wiki/w/Kismet_Feather" },
        new() { Number = 2, Text = "Buy some Kismet Feathers - each one lets you reroll your dungeon chest once for a better item." },
        new() { Number = 3, Text = "Type /warp dungeon_hub to get close.", OnClick = "/warp dungeon_hub" },
        new() { Number = 4, Text = "Queue for Master Mode Floor 7 (needs Catacombs level 36+), with a good party of 5.", OnClick = WhereWikiUrl },
        new() { Number = 5, Text = "Clear the dungeon and beat Necron, then use your Kismet Feathers to reroll the end chest." },
        new() { Number = 6, Text = "You're doing it right when you collect: Wither Essence, and Necron's Handle more often than plain M7.", OnClick = WikiUrl },
        new() { Number = 7, Text = "Sell the Wither Essence, and the Handle if you get one, on the Bazaar/Auction House." },
        new() { Number = 8, Text = "Check /cofl task again to see your real coins/hour." },
    ];
}
