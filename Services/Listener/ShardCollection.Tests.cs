using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class ShardCollectionTests
{
    [TestCase("§a§lCAPTURE! §7You caught a §aAreita§7 and gained a §aAreita Shard§7!", "SHARD_AREITA", 1)]
    [TestCase("You caught x12 Birries Shards!", "SHARD_BIRRIES", 12)]
    [TestCase("LOOT SHARE You received a Chill Shard for assisting Oden.", "SHARD_CHILL", 1)]
    public void ParsesShardGainMessages(string message, string expectedTag, int expectedCount)
    {
        var parsed = CollectionListener.TryParseShardGain(message, out var tag, out var count);

        Assert.That(parsed, Is.True);
        Assert.That(tag, Is.EqualTo(expectedTag));
        Assert.That(count, Is.EqualTo(expectedCount));
    }

    [Test]
    public async Task CompletedNpcTradeTracksReceivedShardAndPayment()
    {
        var args = ChatArgs(
            "§e[NPC] §eHunter Harry§f: §fSay, do you have a use for a §9Hideonwall Shard§f? I found it lying around...",
            "§e[NPC] §eHunter Harry§f: §fI'll give you it in exchange for a §5Purple Gem§f!",
            "§eSelect an option: §a[Trade] §c[No thanks]",
            "§e[NPC] §eHunter Harry§f: §fSweet, 'ppreciate it!",
            "§aYou have been given a §9Hideonwall§a!");

        await new CollectionListener().Process(args);

        Assert.That(args.currentState.ItemsCollectedRecently["SHARD_HIDEONWALL"], Is.EqualTo(1));
        Assert.That(args.currentState.ItemsCollectedRecently["PURPLE_GEM"], Is.EqualTo(-1));
    }

    [Test]
    public async Task OfferedButUncompletedNpcTradeTracksNothing()
    {
        var args = ChatArgs(
            "§e[NPC] §dHuntress Melissa§f: §fDo you want this §5Gemzie Shard§f? I already maxed that Attribute...",
            "§e[NPC] §dHuntress Melissa§f: §fHow about I give you it in exchange for, say, a §5Soothing Incense§f?");

        await new CollectionListener().Process(args);

        Assert.That(args.currentState.ItemsCollectedRecently, Is.Empty);
    }

    [Test]
    public async Task NpcTradeUsesChatHistoryAcrossBatches()
    {
        var offer = "[NPC] Hunter Harry: Say, do you have a use for a Hideonwall Shard?";
        var exchange = "[NPC] Hunter Harry: I'll give you it in exchange for a Purple Gem!";
        var completion = "You have been given a Hideonwall!";
        var args = ChatArgs(completion);
        foreach (var line in new[] { offer, exchange, completion })
            args.currentState.ChatHistory.Enqueue(new ChatMessage { Content = line });

        await new CollectionListener().Process(args);

        Assert.That(args.currentState.ItemsCollectedRecently["SHARD_HIDEONWALL"], Is.EqualTo(1));
        Assert.That(args.currentState.ItemsCollectedRecently["PURPLE_GEM"], Is.EqualTo(-1));
    }

    private static MockedUpdateArgs ChatArgs(params string[] lines) => new()
    {
        currentState = new StateObject(),
        msg = new UpdateMessage
        {
            Kind = UpdateMessage.UpdateKind.CHAT,
            ChatBatch = new List<string>(lines)
        }
    };
}
