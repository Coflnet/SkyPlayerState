using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tasks;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Regression: Kuudra chest essence was never credited (production audit 2026-11-27: 0 ESSENCE_CRIMSON in every stored
/// Kuudra period, T1..T5, 16-104 h each) because DungeonRewardListener returned on every Kuudra chest, and the key a paid
/// chest buys on opening was never charged. The paid chest lore is the verified production sample
/// (DungeonRewardChestParserTests.KuudraPaidChest_ParsesWithZeroCost_AndIsNotADungeonChest); the "Already opened!" ending
/// and the "PAID/FREE CHEST REWARDS" chat lines come from Odin's and vexo's Kuudra trackers, not from our logs.
/// </summary>
public class KuudraChestChargeTests
{
    private const long Purse = 10_000_000;
    private const long BurningKeyCoins = 582_000;

    private static ChestView KuudraChest(string title, string costLine, string ending = "Click to open!")
    {
        var lore = "Contents\nWither Spectre Shard\nKuudra Tentacle\n§dCrimson Essence §8x500\n§5Kuudra Teeth §8x4\nKraken Shard"
            + "\n\nCost\n" + costLine + "\n\n" + ending;
        return new ChestView
        {
            Name = title,
            Items = [new() { ItemName = "§aOpen Reward Chest", Description = lore }]
        };
    }

    private static MockedUpdateArgs Inventory(StateObject state, ChestView chest) => new()
    {
        currentState = state,
        msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.INVENTORY, PlayerId = "p1", Chest = chest }
    };

    private static MockedUpdateArgs Scoreboard(StateObject state, long purse) => new()
    {
        currentState = state,
        msg = new UpdateMessage
        {
            Kind = UpdateMessage.UpdateKind.Scoreboard, PlayerId = "p1",
            Scoreboard = [$"Purse: {purse.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}"]
        }
    };

    private static MockedUpdateArgs Chat(StateObject state, string line) => new()
    {
        currentState = state,
        msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.CHAT, PlayerId = "p1", ChatBatch = [line] }
    };

    private static StateObject InTier()
    {
        var state = new StateObject();
        state.ExtractedInfo.Purse = Purse;
        state.ExtractedInfo.CurrentLocation = "Kuudra's Hollow (T3)";
        return state;
    }

    private static long ChestCost(StateObject state) => state.ItemsCollectedRecently.GetValueOrDefault(PseudoItems.DUNGEON_CHEST_COST);

    [Test]
    public void Parser_ReadsTheKeyOfThePaidChest()
    {
        DungeonRewardChestParser.TryParse(KuudraChest("Paid Chest", "Burning Kuudra Key"), null, out var info).Should().BeTrue();

        info.KuudraKey.Tag.Should().Be("KUUDRA_BURNING_TIER_KEY");
        info.KuudraKey.Coins.Should().Be(BurningKeyCoins);
        info.CostCoins.Should().Be(0, "the parser still never charges a Kuudra chest a floor's coins");
    }

    [Test]
    public async Task PaidChest_ConfirmedByThePurseDrop_ChargesTheKeyAndCreditsTheEssence()
    {
        var state = InTier();
        var listener = new DungeonRewardListener();

        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key")));
        await listener.Process(Scoreboard(state, Purse - BurningKeyCoins));

        ChestCost(state).Should().Be(-BurningKeyCoins);
        state.ItemsCollectedRecently.GetValueOrDefault("ESSENCE_CRIMSON").Should().Be(500);
        state.ExtractedInfo.PendingDungeonChestCharge.Should().BeNull();
    }

    [Test]
    public async Task PaidChest_ViewedButNotOpened_ChargesNothing()
    {
        var state = InTier();
        var listener = new DungeonRewardListener();

        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Infernal Kuudra Key")));
        await listener.Process(Scoreboard(state, Purse));

        state.ItemsCollectedRecently.Should().BeEmpty("no purse drop, so the key was not bought");
    }

    [Test]
    public async Task PaidChest_ViewedTwice_ChargesOnce()
    {
        var state = InTier();
        var listener = new DungeonRewardListener();

        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key")));
        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key")));
        await listener.Process(Scoreboard(state, Purse - BurningKeyCoins));
        await listener.Process(Scoreboard(state, Purse - BurningKeyCoins));

        ChestCost(state).Should().Be(-BurningKeyCoins);
    }

    [Test]
    public async Task PaidChest_AlreadyOpened_IsNotPending()
    {
        var state = InTier();

        await new DungeonRewardListener().Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key", "Already opened!")));

        state.ExtractedInfo.PendingDungeonChestCharge.Should().BeNull();
    }

    [Test]
    public async Task PaidChest_ChatLine_CreditsOnlyTheEssence_AndThePurseStillBooksTheKey()
    {
        var state = InTier();
        var listener = new DungeonRewardListener();

        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key")));
        await listener.Process(Chat(state, "  PAID CHEST REWARDS"));

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { ["ESSENCE_CRIMSON"] = 500 },
            "a pre-bought key shows up as its own -1 inventory delta, so the chat line books no coins");

        await listener.Process(Scoreboard(state, Purse - BurningKeyCoins));

        ChestCost(state).Should().Be(-BurningKeyCoins);
        state.ItemsCollectedRecently["ESSENCE_CRIMSON"].Should().Be(500, "the essence is credited once");
    }

    [Test]
    public async Task PaidChest_BooksTheActualDrop_UpToTheListedPrice()
    {
        var state = InTier();
        var listener = new DungeonRewardListener();

        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key")));
        await listener.Process(Scoreboard(state, Purse - 675_000)); // 10% reputation discount

        ChestCost(state).Should().Be(-675_000);
    }

    [TestCase(100_000, Description = "too small: a cheaper purchase")]
    [TestCase(1_000_000, Description = "above the listed 750k: something else was paid")]
    public async Task PaidChest_DropOutsideTheKeyPrice_ChargesNothing(long drop)
    {
        var state = InTier();
        var listener = new DungeonRewardListener();

        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key")));
        await listener.Process(Scoreboard(state, Purse - drop));

        state.ItemsCollectedRecently.Should().NotContainKey(PseudoItems.DUNGEON_CHEST_COST);
    }

    [Test]
    public async Task PaidChest_BazaarPurchaseSinceTheView_IsTakenOutOfTheDrop()
    {
        var state = InTier();
        var listener = new DungeonRewardListener();

        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key")));
        // a 600k bazaar buy alone would look like the key
        state.ItemsCollectedRecently[PseudoItems.BAZAAR_PURCHASE] = 600_000;
        await listener.Process(Scoreboard(state, Purse - 600_000));

        state.ItemsCollectedRecently.Should().NotContainKey(PseudoItems.DUNGEON_CHEST_COST);

        await listener.Process(Scoreboard(state, Purse - 600_000 - BurningKeyCoins));

        ChestCost(state).Should().Be(-BurningKeyCoins, "the key bought after the bazaar order still confirms");
    }

    [Test]
    public void Essence_WithThousandsSeparator_IsParsed()
    {
        DungeonRewardListener.ParseEssences(["Crimson Essence x2,000", "Wither Essence x54"])
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["ESSENCE_CRIMSON"] = 2_000, ["ESSENCE_WITHER"] = 54 });
        KuudraListener.EssenceCount("§dCrimson Essence §8x2,000").Should().Be(2_000);
    }

    [Test]
    public async Task FreeChest_ConfirmedByChat_CreditsOnlyTheEssence()
    {
        var state = InTier();
        var listener = new DungeonRewardListener();

        await listener.Process(Inventory(state, KuudraChest("Free Chest", "FREE")));
        await listener.Process(Chat(state, "FREE CHEST REWARDS"));

        state.ItemsCollectedRecently.Should().BeEquivalentTo(new Dictionary<string, int> { ["ESSENCE_CRIMSON"] = 500 });
    }

    [Test]
    public async Task FreeChest_DoesNotPushOutAPendingPaidChest()
    {
        var state = InTier();
        var listener = new DungeonRewardListener();

        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key")));
        await listener.Process(Inventory(state, KuudraChest("Free Chest", "FREE")));
        await listener.Process(Scoreboard(state, Purse - BurningKeyCoins));

        ChestCost(state).Should().Be(-BurningKeyCoins);
    }

    [Test]
    public async Task CroesusBulkClaim_ChargesEveryOpenedChest()
    {
        var state = new StateObject();
        state.ExtractedInfo.Purse = Purse;
        state.ExtractedInfo.CurrentLocation = "Dungeon Hub";
        var listener = new DungeonRewardListener();

        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key")));
        // the purse already shows the first key bought when the second chest is viewed
        state.ExtractedInfo.Purse = Purse - BurningKeyCoins;
        await listener.Process(Inventory(state, KuudraChest("Paid Chest", "Burning Kuudra Key")));
        await listener.Process(Scoreboard(state, Purse - 2 * BurningKeyCoins));

        ChestCost(state).Should().Be(-2 * BurningKeyCoins);
        state.ItemsCollectedRecently.GetValueOrDefault("ESSENCE_CRIMSON").Should().Be(1_000);
    }
}
