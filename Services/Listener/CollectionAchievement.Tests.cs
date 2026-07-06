using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class CollectionAchievementTests
{
    private static MockedUpdateArgs ArgsFor(StateObject state)
    {
        var args = new MockedUpdateArgs { currentState = state };
        args.AddService<IAchievementService>(new AchievementService());
        args.AddService<Microsoft.Extensions.Logging.ILogger<CollectionListener>>(NullLogger<CollectionListener>.Instance);
        return args;
    }

    [Test]
    public void MoreThanTwentyThousandOfAKind_UnlocksFarmer()
    {
        var args = ArgsFor(new StateObject());

        CollectionListener.UnlockCollectionAchievements(args, new Dictionary<string, int> { ["WHEAT"] = 20_001 });

        Assert.That(args.currentState.UnlockedAchievements, Does.Contain(Achievement.Farmer));
        Assert.That(args.currentState.UnlockedAchievements, Does.Not.Contain(Achievement.Collector));
    }

    [Test]
    public void ExactlyTwentyThousand_DoesNotUnlockFarmer()
    {
        var args = ArgsFor(new StateObject());

        CollectionListener.UnlockCollectionAchievements(args, new Dictionary<string, int> { ["WHEAT"] = 20_000 });

        Assert.That(args.currentState.UnlockedAchievements, Is.Empty);
    }

    [Test]
    public void ManySmallCounts_DoNotSumTowardsFarmerThreshold()
    {
        var args = ArgsFor(new StateObject());

        CollectionListener.UnlockCollectionAchievements(args, new Dictionary<string, int>
        {
            ["WHEAT"] = 10_000,
            ["CARROT"] = 10_000,
            ["POTATO"] = 10_000
        });

        Assert.That(args.currentState.UnlockedAchievements, Is.Empty);
    }

    [Test]
    public void FiftyDifferentItemKinds_UnlocksCollector()
    {
        var args = ArgsFor(new StateObject());
        var itemsCollected = Enumerable.Range(0, 50).ToDictionary(i => $"ITEM_{i}", i => 1);

        CollectionListener.UnlockCollectionAchievements(args, itemsCollected);

        Assert.That(args.currentState.UnlockedAchievements, Does.Contain(Achievement.Collector));
        Assert.That(args.currentState.UnlockedAchievements, Does.Not.Contain(Achievement.Farmer));
    }

    [Test]
    public void FortyNineDifferentItemKinds_DoesNotUnlockCollector()
    {
        var args = ArgsFor(new StateObject());
        var itemsCollected = Enumerable.Range(0, 49).ToDictionary(i => $"ITEM_{i}", i => 1);

        CollectionListener.UnlockCollectionAchievements(args, itemsCollected);

        Assert.That(args.currentState.UnlockedAchievements, Is.Empty);
    }

    [Test]
    public void BothThresholdsMet_UnlocksBothAchievements()
    {
        var args = ArgsFor(new StateObject());
        var itemsCollected = Enumerable.Range(0, 50).ToDictionary(i => $"ITEM_{i}", i => 1);
        itemsCollected["WHEAT"] = 25_000;

        CollectionListener.UnlockCollectionAchievements(args, itemsCollected);

        Assert.That(args.currentState.UnlockedAchievements, Does.Contain(Achievement.Collector));
        Assert.That(args.currentState.UnlockedAchievements, Does.Contain(Achievement.Farmer));
    }
}
