using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

public class TheEndTask : IslandTask
{
    protected override string RegionName => "the end";
    protected override HashSet<string> locationNames =>
    [
        "Dragon's Nest",
        "Void Sepulture",
        "Void Slate",
        "Zealot Bruiser Hideout"
    ];
}
