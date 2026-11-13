using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

public class TheParkTask : IslandTask
{
    protected override string RegionName => "the park";
    protected override HashSet<string> locationNames => ["The Park"];
}

public class TheParkForagingTask : IslandTask
{
    protected override string RegionName => "the park foraging";
    protected override HashSet<string> locationNames =>
    [
        "Birch Park",
        "Dark Thicket",
        "Savanna Woodland",
        "Spruce Woods"
    ];
}
