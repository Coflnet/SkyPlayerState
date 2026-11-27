using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

public class JerryTask : IslandTask
{
    protected override string RegionName => "jerry";
    protected override HashSet<string> locationNames =>
    [
        "Jerry's Workshop",
        "Jerry Pond",
        "Sunken Jerry Pond",
        "Reflective Pond",
        "Mount Jerry",
        "Glacial Cave",
        "Hot Springs",
        "Gary's Shack",
        "Terry's Shack"
    ];

    /// <summary>The island opens for all of Late Winter and throughout real-world December, see <see cref="SkyblockEvents.JerrysWorkshop"/>.</summary>
    public override bool IsPossibleAt(DateTime time) => SkyblockEvents.JerrysWorkshop.IsRunningAt(time);
}
