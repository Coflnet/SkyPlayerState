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

    // Regression for production zone "Critter Safari": Hypixel writes the gained count as "2x"
    // (digits-then-x) here, not "x2" - the old regex only recognized "x2" and the amount group fell
    // through to the "caught/received" fallback regex, whose lazy match then swallowed the rest of
    // the sentence up to "Shards!", producing bogus tags like "SHARD_BLUEBIRD_AND_GAINED_2X_BLUEBIRD"
    // and "SHARD_FOXTROT_AND_GAINED_2X_FOXTROT" (verbatim from production) instead of the real
    // "SHARD_BLUEBIRD"/"SHARD_FOXTROT".
    [TestCase("You caught a Bluebird and gained 2x Bluebird Shards!", "SHARD_BLUEBIRD", 2)]
    [TestCase("You caught a Foxtrot and gained 2x Foxtrot Shards!", "SHARD_FOXTROT", 2)]
    public void ParsesDigitsBeforeXGainedCountIntoTheRealShardTag(string message, string expectedTag, int expectedCount)
    {
        var parsed = CollectionListener.TryParseShardGain(message, out var tag, out var count);

        Assert.That(parsed, Is.True);
        Assert.That(tag, Is.EqualTo(expectedTag));
        Assert.That(count, Is.EqualTo(expectedCount));
        Assert.That(tag, Does.Not.Contain("AND_GAINED"));
    }

    [Test]
    public async Task SafariCatchLinesForTheSameMobAccumulateUnderOneRealShardTag()
    {
        // One catch reported with the "a" count format, one with the "2x" format - both must land
        // on the single real SHARD_BLUEBIRD tag, not be split across a real and a bogus fallback tag.
        var args = ChatArgs(
            "You caught a Bluebird and gained a Bluebird Shard!",
            "You caught a Bluebird and gained 2x Bluebird Shards!");

        await new CollectionListener().Process(args);

        Assert.That(args.currentState.ItemsCollectedRecently.GetValueOrDefault("SHARD_BLUEBIRD"), Is.EqualTo(3));
        Assert.That(args.currentState.ItemsCollectedRecently, Has.Count.EqualTo(1));
    }

    [Test]
    public void SuspiciousDerivedShardNameIsRejectedNotStored()
    {
        // General guard (independent of the specific "2x" fix above): an unmapped shard name whose
        // text still contains sentence-fragment leftovers (" and ", a leftover "5x"/"x5" count token)
        // must never be stored as a per-mob tag.
        var parsed = CollectionListener.TryParseShardGain(
            "You received a Widget and 5x Sprocket Shard!", out var tag, out var count);

        Assert.That(parsed, Is.False);
        Assert.That(tag, Is.Empty);
        Assert.That(count, Is.Zero);
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

    [Test]
    public async Task SafariRewardSummaryReconcilesCapturedShards()
    {
        var args = ChatArgs("""
            SAFARI_SHARD_REWARDS 32
            Chuckwalla x1
            Fluffling x3
            Mantis Shrimp x5
            Parakeet x1
            Bluebird x1
            Polaris x4
            Treefrog x6
            Woodchucker x1
            Foxtrot x4
            Shyworm x3
            Strongarm x2
            Tepid x1
            """);
        args.currentState.ItemsCollectedRecently = new()
        {
            ["SHARD_MANTIS_SHRIMP"] = 4,
            ["SHARD_TEPID"] = 1,
            ["SHARD_WRONG"] = 2,
            ["SAFARI_ESSENCE"] = 225
        };

        await new CollectionListener().Process(args);

        Assert.That(args.currentState.ItemsCollectedRecently["SHARD_MANTIS_SHRIMP"], Is.EqualTo(5));
        Assert.That(args.currentState.ItemsCollectedRecently["SHARD_TREEFROG"], Is.EqualTo(6));
        Assert.That(args.currentState.ItemsCollectedRecently["SHARD_TEPID"], Is.EqualTo(1));
        Assert.That(args.currentState.ItemsCollectedRecently, Does.Not.ContainKey("SHARD_WRONG"));
        Assert.That(args.currentState.ItemsCollectedRecently["SAFARI_ESSENCE"], Is.EqualTo(225));
    }

    [Test]
    public void SafariRewardSummaryRejectsIncompleteHoverBreakdown()
    {
        var parsed = CollectionListener.TryParseSafariShardRewards(
            "SAFARI_SHARD_REWARDS 32\nMantis Shrimp x5\nTepid x1", out _);

        Assert.That(parsed, Is.False);
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
