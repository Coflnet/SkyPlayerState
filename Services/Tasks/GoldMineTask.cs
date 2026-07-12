using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

public class GoldMineTask : IslandTask
{
    protected override string RegionName => "gold mine";
    protected override HashSet<string> locationNames =>
    [
        "Gold Mine"
    ];
}
