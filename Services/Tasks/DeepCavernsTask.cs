using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

public class DeepCavernsTask : IslandTask
{
    protected override string RegionName => "deep caverns";
    protected override HashSet<string> locationNames =>
    [
        "Deep Caverns",
        "Diamond Reserve",
        "Emerald Reserve",
        "Gold Reserve",
        "Gunpowder Mines",
        "Lapis Quarry",
        "Obsidian Sanctuary",
        "Pigmen's Den",
        "Redstone Quarry",
        "Slimehill"
    ];
}
