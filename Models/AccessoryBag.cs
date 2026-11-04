using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Models;

/// <summary>
/// A slim accessory (no lore) stored from the Accessory Bag pages
/// </summary>
public class BagAccessory
{
    /// <summary>Item tag, eg WEDDING_RING_7</summary>
    public string Tag { get; set; }
    /// <summary>Display name without color codes</summary>
    public string ItemName { get; set; }
    /// <summary>Rarity index from the item's tier attribute (0 common ... 5 mythic, see Coflnet.Sky.Core.Tier)</summary>
    public int Tier { get; set; }
    /// <summary>Reforge name</summary>
    public string Modifier { get; set; }
    /// <summary>True if the accessory was recombobulated</summary>
    public bool Recombobulated { get; set; }
    /// <summary>Enrichment, if any</summary>
    public string Enrichment { get; set; }
    /// <summary>Magical power the accessory grants, 0 if unknown</summary>
    public int AccessoryPower { get; set; }
    /// <summary>1 based page of the bag</summary>
    public int Page { get; set; }
    /// <summary>Slot index within the page</summary>
    public int Slot { get; set; }
}

/// <summary>
/// All accessories seen in a player's accessory bag
/// </summary>
public class AccessoryBag
{
    /// <summary>Accessories of all seen pages</summary>
    public List<BagAccessory> Accessories { get; set; } = new();
    /// <summary>Profile the bag belongs to, Guid.Empty if the profile was unknown when it was stored</summary>
    public Guid ProfileId { get; set; }
    /// <summary>Number of pages the bag has</summary>
    public int PageCount { get; set; }
    /// <summary>Pages that were opened and stored</summary>
    public List<int> SeenPages { get; set; } = new();
    /// <summary>When the newest page was stored</summary>
    public DateTime UpdatedAt { get; set; }
}
