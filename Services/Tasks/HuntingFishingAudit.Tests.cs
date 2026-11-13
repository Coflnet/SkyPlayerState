using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class HuntingFishingAuditTests
{
    [TestCase(0, false, 35)]
    [TestCase(34, false, 35)]
    [TestCase(35, true, -1)]
    [TestCase(54, true, -1)]
    [TestCase(55, false, 95)]
    [TestCase(59, false, 95)]
    public async Task RainSlimeUsesCurrentWeatherWindow(int minute, bool available, int nextMinute)
    {
        var start = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
        var result = await new RainSlimeHuntingTask().ExecuteWithRequirements(new TaskParams
        {
            TestTime = start.AddMinutes(minute),
            ExtractedInfo = new ExtractedInfo(),
            Formatter = new SimpleTaskFormatProvider(),
            Cache = new ConcurrentDictionary<Type, TaskParams.CalculationCache>(),
            LocationProfit = [], CleanPrices = [], BazaarPrices = [], Names = []
        });
        result.IsAccessible.Should().Be(available);
        result.NextAvailableAt.Should().Be(nextMinute < 0 ? null : start.AddMinutes(nextMinute));
    }

    [TestCase("ENCHANTED_MUTTON")]
    [TestCase("ENCHANTED_COOKED_MUTTON")]
    [TestCase("ENCHANTED_RABBIT")]
    public void OasisExclusiveSeaCreatureDropProvesFishingWithoutRawFish(string tag)
    {
        new TaskClassifier(new TaskRegistry()).Classify("Oasis", new Dictionary<string, int> { [tag] = 1 }, 10)
            ?.TaskName.Should().Be("Oasis Fishing");
    }

    [Test]
    public void SafariSharedCatchDoesNotRequirePartyMembership()
    {
        var task = new CritterSafariPartyTask();
        task.GetDetectionSignature().MethodName.Should().Be("Critter Safari Party (Loot Share)");
        task.Description.Should().Contain("same Safari instance");
        string.Join(" ", task.StepsForTest.Select(step => step.Text)).Should().Contain("party membership is not required");
    }

    [TestCase(typeof(RainSlimeHuntingTask), "Rain_Slime")]
    [TestCase(typeof(HellwispHuntingTask), "Hellwisp")]
    [TestCase(typeof(WitherSpecterHuntingTask), "Wither_Spectre")]
    [TestCase(typeof(BruiserHuntingTask), "Zealot_Bruiser")]
    [TestCase(typeof(HuntingTrapTask), "Huntraps")]
    [TestCase(typeof(SpookyFishingTask), "Spooky_Festival")]
    public void GuidanceLinksToTheRelevantMechanicOrCreature(Type type, string page)
    {
        var task = (MethodTask)Activator.CreateInstance(type);
        task.WikiUrlForTest.Should().Be("https://hypixelskyblock.minecraft.wiki/w/" + page);
    }
    [TestCase("Cod", "Galatea Fishing (Hunting)")]
    [TestCase("Verdant", "Galatea Fishing (Hunting)")]
    [TestCase("Azure", "Galatea Fishing (Hunting)")]
    [TestCase("Salmon", "Galatea Fishing (Hunting)")]
    [TestCase("Bogged", "Moonglade Sea Creatures (Hunting)")]
    [TestCase("Loch Emperor", "Moonglade Sea Creatures (Hunting)")]
    [TestCase("Nessie", "Moonglade Sea Creatures (Hunting)")]
    public void RealShardChatNamesReachTheCorrectNetOrRodTask(string name, string expected)
    {
        Coflnet.Sky.PlayerState.Services.CollectionListener.TryParseShardGain(
            $"You received a {name} Shard!", out var tag, out var count).Should().BeTrue();
        new TaskClassifier(new TaskRegistry()).Classify("Murkwater Loch", new Dictionary<string, int> { [tag] = count }, 10)
            ?.TaskName.Should().Be(expected);
    }

    [TestCase("SHARD_HIDEONLEAF")]
    [TestCase("SHARD_BIRRIES")]
    [TestCase("SEA_LUMIES")]
    [TestCase("MANGROVE_LOG")]
    public void UnrelatedMoongladeActivitiesDoNotProveFishing(string tag)
    {
        var result = new TaskClassifier(new TaskRegistry()).Classify("Murkwater Loch", new Dictionary<string, int> { [tag] = 20 }, 10);
        result?.TaskName.Should().NotBe("Galatea Fishing");
        result?.TaskName.Should().NotBe("Galatea Fishing (Hunting)");
        result?.TaskName.Should().NotBe("Moonglade Sea Creatures (Hunting)");
    }

    [TestCase("WET_WATER")]
    [TestCase("GILL_MEMBRANE")]
    [TestCase("DIVER_FRAGMENT")]
    [TestCase("FLEXBONE")]
    [TestCase("SWAMP_THE_FISH")]
    public void ExclusiveMoongladeRodCatchProvesFishing(string tag)
    {
        new TaskClassifier(new TaskRegistry()).Classify("Murkwater Loch", new Dictionary<string, int> { [tag] = 1 }, 10)
            ?.TaskName.Should().Be("Galatea Fishing");
    }

    [Test]
    public void LotusCurrenciesAreNotInventedTrophyFrogs()
    {
        LotusAtollTask.FrogSpecies.Should().NotContain("LOTUS");
        var items = new LotusAtollTask().GetDetectionSignature().DetectionItems;
        items.Should().NotContain("LOTUS_BRONZE");
        items.Should().Contain("LOTUS_SILVER");
        items.Should().Contain("COMMON_FROG_BRONZE");
    }

}
