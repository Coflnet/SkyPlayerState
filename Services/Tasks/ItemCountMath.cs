using System;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Saturating arithmetic for the persisted <c>Dictionary&lt;string,int&gt;</c> item-count maps.
/// Coin-denominated pseudo tags (<see cref="PseudoItems"/>) can approach <see cref="int.MaxValue"/>,
/// so plain int addition would silently wrap negative (unchecked) or throw (checked).
/// </summary>
public static class ItemCountMath
{
    /// <summary>Adds <paramref name="delta"/> to the tag's count, clamping to int.MinValue/MaxValue.</summary>
    public static void Add(Dictionary<string, int> counts, string tag, long delta)
    {
        counts[tag] = Saturate(counts.GetValueOrDefault(tag, 0) + delta);
    }

    public static int Saturate(long value) =>
        value > int.MaxValue ? int.MaxValue : value < int.MinValue ? int.MinValue : (int)value;
}
