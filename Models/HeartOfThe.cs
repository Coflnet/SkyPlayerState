using System.Collections.Generic;
using MessagePack;

namespace Coflnet.Sky.PlayerState.Models;

[MessagePackObject]
public class HeartOfThe
{
    [Key(0)]
    public int Tier { get; set; }
    [Key(1)]
    public Dictionary<string, int> Perks { get; set; } = [];
}
#nullable restore