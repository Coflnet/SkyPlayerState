using System;

namespace Coflnet.Sky.PlayerState.Models;

public static class SpecialTransactionItemIds
{
    public const long Coins = 1_000_001;
}

public class Transaction
{
    public Guid PlayerUuid { get; set; }
    public Guid ProfileUuid { get; set; }
    public TransactionType Type { get; set; }
    public long ItemId { get; set; }
    public long Amount { get; set; }
    public DateTime TimeStamp { get; set; }

    [Flags]
    public enum TransactionType
    {
        UNKOWN,
        RECEIVE = 1,
        /// <summary>
        /// Item left inventory, if alone means dropped
        /// </summary>
        REMOVE = 2,
        BAZAAR = 4,
        AH = 8,
        NPC = 16,
        TRADE = 32,
        /// <summary>
        /// Picking up or dropping
        /// </summary>
        WORLD = 64,
        CRAFT = 128,
        CHEST = 256,
        STASH = 512,
        BACKPACK = 1024,
        ENDERCHEST = 2048,
        BAG = 4096,
        /// <summary>
        /// Item is still in reach for the player
        /// </summary>
        Move = 8192,
        BazaarSell = BAZAAR | REMOVE,
        BazaarBuy = BAZAAR | RECEIVE,
        BazaarListSell = BAZAAR | Move | REMOVE,
        BazaarListBuy = BAZAAR | Move | RECEIVE,
        AHBuy = AH | RECEIVE,
        AHSell = AH | REMOVE,
        NPCBuy = NPC | RECEIVE,
        NPCSell = NPC | REMOVE,
        TradeGive = TRADE | REMOVE,
        TradeReceive = TRADE | RECEIVE,
        CraftIngredient = CRAFT | REMOVE,
        CraftResult = CRAFT | RECEIVE,

    }

    public Transaction(Transaction t)
    {
        PlayerUuid = t.PlayerUuid;
        ProfileUuid = t.ProfileUuid;
        Type = t.Type;
        ItemId = t.ItemId;
        Amount = t.Amount;
        TimeStamp = t.TimeStamp;
    }

    public Transaction()
    {
    }

    public override string ToString()
    {
        return $"{Type}({(int)Type}) {ItemId} {Amount} {TimeStamp} {PlayerUuid} ";
    }
}

/// <summary>
/// Maps movements of items 
/// </summary>
public class ItemTransaction : Transaction
{
    public ItemTransaction()
    {
    }

    public ItemTransaction(Transaction t) : base(t)
    {
    }
}

/// <summary>
/// Maps transactions of a player
/// </summary>
public class PlayerTransaction : Transaction
{
    public PlayerTransaction()
    {
    }

    public PlayerTransaction(Transaction t) : base(t)
    {
    }

}
#nullable restore