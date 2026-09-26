using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

// ── Base class for dungeon tasks ──
public abstract class BaseDungeonTask : MethodTask
{
    protected override string Category => "Dungeon";
    protected override string ActionUnit => "runs";
    protected override List<DropEffect> Effects =>
    [
        new() { Name = "Dungeon Class Level", Description = "Higher class level increases damage and survivability", EstimatedMultiplier = 1.2 },
        new() { Name = "Catacombs Level", Description = "Higher catacombs level unlocks better drops", EstimatedMultiplier = 1.3 },
        new() { Name = "Kismet Feather", Description = "Rerolls dungeon chest drops for better loot", EstimatedMultiplier = 1.5 }
    ];
}

public class M4Task : BaseDungeonTask
{
    protected override string MethodName => "M4";
    protected override HashSet<string> Locations => ["The Catacombs", "Master Mode Catacombs Floor IV"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 300)];
    protected override string HowTo => "Queue for Master Mode Floor 4 in the Dungeon Hub. Requires Catacombs level 26+. Run with a party of 5 for efficient clears.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "WITHER_CHESTPLATE", Reason = "Dungeon armor for survivability" }
    ];
}
public class M5Task : BaseDungeonTask
{
    protected override string MethodName => "M5";
    protected override HashSet<string> Locations => ["The Catacombs", "Master Mode Catacombs Floor V"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 400)];
    protected override string HowTo => "Queue for Master Mode Floor 5. Requires Catacombs level 28+. Boss fight is Professor with Guardians phase.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "WITHER_CHESTPLATE", Reason = "Dungeon armor for survivability" }
    ];
}
public class M6Task : BaseDungeonTask
{
    protected override string MethodName => "M6";
    protected override HashSet<string> Locations => ["The Catacombs", "Master Mode Catacombs Floor VI"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 500)];
    protected override string HowTo => "Queue for Master Mode Floor 6. Requires Catacombs level 30+. Boss is Sadan with terracotta phases.";
    protected override List<RequiredItem> RequiredItems => [
        new() { ItemTag = "WITHER_CHESTPLATE", Reason = "Dungeon armor for survivability" },
        new() { ItemTag = "HYPERION", Reason = "Mage weapon for efficient clears" }
    ];
}
public class M7Task : BaseDungeonTask
{
    protected override string MethodName => "M7";
    protected override HashSet<string> Locations => ["The Catacombs", "Master Mode", "Floor VII", "Master Mode Catacombs Floor VII"];
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
    protected override HashSet<string> Locations => ["The Catacombs", "Master Mode", "Floor VII", "Master Mode Catacombs Floor VII"];
    protected override HashSet<string> DetectionItems => ["KISMET_FEATHER"];
    protected override List<MethodDrop> FormulaDrops => [new("ESSENCE_WITHER", 600), new("NECRON_HANDLE", 0.1)];
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
