using System.Collections.Generic;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class MithrilPowderTrackingTests
{
    private static readonly System.DateTime T0 = new(2026, 11, 10, 12, 0, 0, System.DateTimeKind.Utc);

    private static MockedUpdateArgs ArgsAt(System.DateTime receivedAt) => new()
    {
        currentState = new StateObject(),
        msg = new UpdateMessage { ReceivedAt = receivedAt }
    };

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
        var args = ArgsAt(T0.AddMinutes(5));
        args.currentState.ExtractedInfo.MithrilPowder = 1000;
        args.currentState.ExtractedInfo.PowderSeenAt = T0;

        CollectionListener.TrackMithrilPowder(args, 1125);

        Assert.That(args.currentState.ExtractedInfo.MithrilPowder, Is.EqualTo(1125));
        Assert.That(args.currentState.ItemsCollectedRecently.GetValueOrDefault("MITHRIL_POWDER"), Is.EqualTo(125));
    }

    [Test]
    public void TrackMithrilPowder_Decrease_DoesNotTrackNegativeDiff()
    {
        var args = ArgsAt(T0.AddMinutes(5));
        args.currentState.ExtractedInfo.MithrilPowder = 1200;
        args.currentState.ExtractedInfo.PowderSeenAt = T0;
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
