using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

public class DungeonHubTask : IslandTask
{
    protected override string RegionName => "dungeon hub";
    protected override HashSet<string> locationNames =>
    [
        "Dungeon Hub",
        "Dungeon Hub - Main"
    ];
}
