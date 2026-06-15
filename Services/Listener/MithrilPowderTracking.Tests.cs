using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class MithrilPowderTrackingTests
{
    [Test]
    public void ParseMithrilPowderFromTab_LabelFormat_ReturnsAmount()
    {
        var tab = new[]
        {
            "Profile: Banana",
            "§7Mithril Powder: §a123,456"
        };

        var result = CollectionListener.ParseMithrilPowderFromTab(tab);

        Assert.That(result, Is.EqualTo(123456));
    }

    [Test]
    public void TrackMithrilPowder_Increase_TracksCollectedDiff()
    {
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject()
        };
        args.currentState.ExtractedInfo.MithrilPowder = 1000;

        CollectionListener.TrackMithrilPowder(args, 1125);

        Assert.That(args.currentState.ExtractedInfo.MithrilPowder, Is.EqualTo(1125));
        Assert.That(args.currentState.ItemsCollectedRecently.GetValueOrDefault("MITHRIL_POWDER"), Is.EqualTo(125));
    }

    [Test]
    public void TrackMithrilPowder_Decrease_DoesNotTrackNegativeDiff()
    {
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject()
        };
        args.currentState.ExtractedInfo.MithrilPowder = 1200;
        args.currentState.ItemsCollectedRecently["MITHRIL_POWDER"] = 50;

        CollectionListener.TrackMithrilPowder(args, 1000);

        Assert.That(args.currentState.ExtractedInfo.MithrilPowder, Is.EqualTo(1000));
        Assert.That(args.currentState.ItemsCollectedRecently.GetValueOrDefault("MITHRIL_POWDER"), Is.EqualTo(50));
    }

    [Test]
    public void ParseMithrilPowderFromHeartOfThe_ExtractsFromPowderItem()
    {
        var chest = new ChestView
        {
            Name = "Heart of the Mountain",
            Items = new List<Item>
            {
                new()
                {
                    ItemName = "§2Mithril Powder",
                    Description = "§7You have §a245,678 Mithril Powder"
                }
            }
        };

        var result = HeartOfTheListener.ParseMithrilPowderFromHeartOfThe(chest);

        Assert.That(result, Is.EqualTo(245678));
    }
}
