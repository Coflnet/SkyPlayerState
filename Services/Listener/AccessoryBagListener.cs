using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Stores the accessories shown on the "Accessory Bag" pages
/// </summary>
public class AccessoryBagListener : UpdateListener
{
    private const int PlayerInventorySlots = 36;
    private const int NavigationRowSlots = 9;
    private static readonly Regex PageName = new(@"^Accessory Bag(?: \((\d+)/(\d+)\))?$", RegexOptions.Compiled);
    private static readonly Regex PowerLine = new(@"Accessory Power: \+?(\d+)", RegexOptions.Compiled);
    /// <summary>Index order of the numeric tier attribute (Coflnet.Sky.Core.Tier is offset by UNKNOWN, so not reusable)</summary>
    private static readonly string[] TierNames = { "COMMON", "UNCOMMON", "RARE", "EPIC", "LEGENDARY", "MYTHIC", "SPECIAL", "VERY_SPECIAL" };
    private static readonly Regex ColorCodes = new("§.", RegexOptions.Compiled);

    /// <summary>Enrichment only, a failure must not drop the inventory update</summary>
    public override bool Optional => true;

    /// <inheritdoc/>
    public override async Task Process(UpdateArgs args)
    {
        if (args.msg.Kind != UpdateMessage.UpdateKind.INVENTORY || IsAlphaNetwork(args))
            return;
        var chest = args.msg.Chest;
        var page = ParsePageName(chest?.Name);
        if (!ShouldStore(page, chest?.Items, args.currentState.McInfo?.Uuid))
            return;
        var accessories = ExtractAccessories(chest.Items, page.Value.page);
        // Guid.Empty while the profile is unknown (see ProfileSwitchListener)
        await args.GetService<AccessoryBagService>().SavePage(
            args.currentState.McInfo.Uuid, args.currentState.ExtractedInfo.CurrentProfileId, page.Value.page, page.Value.pageCount, accessories);
        Logger.LogInformation("Saved accessory bag page {page}/{count} of {playerId} with {accessories} accessories",
            page.Value.page, page.Value.pageCount, args.currentState.PlayerId, accessories.Count);
    }

    /// <summary>Only bag pages of a known player are stored, unknown players would share one partition</summary>
    public static bool ShouldStore((int page, int pageCount)? page, List<Item> items, Guid? playerUuid)
        => page != null && items != null && playerUuid != null && playerUuid != Guid.Empty;

    /// <summary>
    /// Parses "Accessory Bag (2/3)" (or plain "Accessory Bag" as 1/1), null if it is no bag page
    /// </summary>
    public static (int page, int pageCount)? ParsePageName(string name)
    {
        var match = name == null ? null : PageName.Match(name);
        if (match == null || !match.Success)
            return null;
        if (!match.Groups[1].Success)
            return (1, 1);
        return (int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
    }

    /// <summary>
    /// Extracts accessories from the chest rows above the navigation row, the trailing player inventory is ignored
    /// </summary>
    public static List<BagAccessory> ExtractAccessories(List<Item> items, int page)
    {
        var bagSlots = items.Count - PlayerInventorySlots - NavigationRowSlots;
        var result = new List<BagAccessory>();
        for (var slot = 0; slot < bagSlots; slot++)
        {
            var item = items[slot];
            if (item?.Tag == null)
                continue;
            result.Add(ToAccessory(item, page, slot));
        }
        return result;
    }

    private static BagAccessory ToAccessory(Item item, int page, int slot)
    {
        var attributes = item.ExtraAttributes;
        return new BagAccessory
        {
            Tag = item.Tag,
            ItemName = StripColors(item.ItemName),
            Tier = ReadTier(attributes),
            Modifier = ReadString(attributes, "modifier"),
            Recombobulated = ReadInt(attributes, "rarity_upgrades") > 0,
            Enrichment = ReadString(attributes, "talisman_enrichment"),
            AccessoryPower = ParsePower(item.Description),
            Page = page,
            Slot = slot
        };
    }

    /// <summary>Reads the "Accessory Power: +12" line anywhere in the lore, 0 if missing</summary>
    public static int ParsePower(string description)
    {
        var match = PowerLine.Match(StripColors(description) ?? "");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    /// <summary>The tier attribute is an index (0 common) or a rarity name like "LEGENDARY"</summary>
    public static int ReadTier(Dictionary<string, object> attributes)
    {
        var text = ReadString(attributes, "tier");
        if (int.TryParse(text, out var index))
            return index;
        var named = Array.FindIndex(TierNames, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
        return Math.Max(named, 0);
    }

    private static string StripColors(string text) => text == null ? null : ColorCodes.Replace(text, "");

    private static string ReadString(Dictionary<string, object> attributes, string key)
        => attributes != null && attributes.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static int ReadInt(Dictionary<string, object> attributes, string key)
        => int.TryParse(ReadString(attributes, key), out var number) ? number : 0;
}
