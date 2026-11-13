using System;
using System.Collections.Generic;
using Coflnet.Sky.Core;

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

    public override bool IsPossibleAt(DateTime time)
    {
        // The island opens for all of Late Winter and throughout real-world December.
        var currentDay = (int)(Constants.SkyblockYear(time) * 31 * 12) % (31 * 12);
        return time.Month == 12 || currentDay >= 11 * 31;
    }
}
