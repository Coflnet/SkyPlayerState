using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

public class BackwaterBayouTask : IslandTask
{
    protected override string RegionName => "backwater bayou";
    protected override HashSet<string> locationNames =>
    [
        "Backwater Bayou"
    ];
}
