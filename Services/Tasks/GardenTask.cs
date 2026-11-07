using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

public class GardenTask : IslandTask
{
    protected override string RegionName => "the garden";
    // SkyblockZones.Canonical collapses every renamed plot ("Plot - 8", "Plot - Left Farm 2", ...) to the single
    // "Plot" zone and the pest count suffix to "The Garden", so those are the only two zones that ever occur.
    protected override HashSet<string> locationNames => ["The Garden", "Plot"];
}
