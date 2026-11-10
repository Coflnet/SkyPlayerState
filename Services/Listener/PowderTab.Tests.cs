using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Regression: the tab parser only knew "Mithril Powder: N", but the current Powders widget uploads
/// "Powders:" plus " Mithril: N" / " Gemstone: N" / " Glacite: N", so no powder gain was ever booked.
/// Gains are only booked on the baseline's profile, off the alpha network, over a recent baseline and below a rate cap.
/// </summary>
public class PowderTabTests
{
    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    // a Dwarven Mines tab upload as the mod sends it (display names, colors stripped, player list order)
    private static readonly string[] DwarvenMinesTab =
    [
        "         Info",
        " Glacite: 12,345",
        "Area: Dwarven Mines",
        "Commissions:",
        " Mithril Miner: 45%",
        " Titanium Miner: DONE",
        " Mithril: 1,234,567",
        "Powders:",
        " Gemstone: 123,456",
        " Mining Speed: ⸕1,850",
        "§footer:Active Effects\nNo effects active.",
        "Profile: Banana",
    ];

    [Test]
    public void ParsesTheCurrentPowdersWidgetInAnyLineOrder()
    {
        var amounts = PowderTab.Parse(DwarvenMinesTab);

        Assert.That(amounts, Is.EquivalentTo(new Dictionary<string, int>
        {
            { "MITHRIL_POWDER", 1_234_567 },
            { "GEMSTONE_POWDER", 123_456 },
            { "GLACITE_POWDER", 12_345 },
        }));
    }

    [Test]
    public void ShortLinesWithoutThePowdersHeaderAreIgnored()
    {
        var amounts = PowderTab.Parse(["Area: Dwarven Mines", " Mithril: 1,234,567", " Gemstone: 123,456"]);

        Assert.That(amounts, Is.Empty);
    }

    [Test]
    public void OldLabelFormatStillParses()
    {
        var amounts = PowderTab.Parse(["§7Mithril Powder: §a123,456", "Gemstone Powder: 7890", "2,500 Glacite Powder"]);

        Assert.That(amounts, Is.EquivalentTo(new Dictionary<string, int>
        {
            { "MITHRIL_POWDER", 123_456 },
            { "GEMSTONE_POWDER", 7_890 },
            { "GLACITE_POWDER", 2_500 },
        }));
    }

    [TestCase("Mithril Powder: 1.2M")]
    [TestCase("Mithril Powder: 1.234.567")]
    [TestCase("Mithril Powder: 12k")]
    [TestCase("Mithril Powder: 1,2345")]
    [TestCase("1.2M Mithril Powder")]
    public void PartialNumbersAreRejected(string line)
    {
        Assert.That(PowderTab.Parse([line]), Is.Empty);
    }

    [Test]
    public void ShortWidgetLineWithPartialNumberIsRejected()
    {
        Assert.That(PowderTab.Parse(["Powders:", " Mithril: 1.2M"]), Is.Empty);
    }

    [Test]
    public void ParseMithrilPowderFromTabReadsTheCurrentWidget()
    {
        Assert.That(CollectionListener.ParseMithrilPowderFromTab(DwarvenMinesTab), Is.EqualTo(1_234_567));
    }

    [Test]
    public async Task TabUploadsBookTheGainOfEveryPowder()
    {
        var state = new StateObject();

        await ProcessTab(state, DwarvenMinesTab, 0);
        await ProcessTab(state, ["Powders:", " Mithril: 1,235,067", " Gemstone: 123,756", " Glacite: 12,345", "Area: Dwarven Mines"], 1);

        Assert.That(state.ItemsCollectedRecently, Is.EquivalentTo(new Dictionary<string, int>
        {
            { "MITHRIL_POWDER", 500 },
            { "GEMSTONE_POWDER", 300 },
        }));
        Assert.That(state.ExtractedInfo.MithrilPowder, Is.EqualTo(1_235_067));
        Assert.That(state.ExtractedInfo.GemstonePowder, Is.EqualTo(123_756));
        Assert.That(state.ExtractedInfo.GlacitePowder, Is.EqualTo(12_345));
        Assert.That(state.ExtractedInfo.PowderSeenAt, Is.EqualTo(T0.AddMinutes(1)));
    }

    [Test]
    public async Task SpendingPowderBooksNothing()
    {
        var state = new StateObject();

        await ProcessTab(state, DwarvenMinesTab, 0);
        await ProcessTab(state, ["Powders:", " Mithril: 234,567", " Gemstone: 23,456", " Glacite: 2,345"], 1);

        Assert.That(state.ItemsCollectedRecently, Is.Empty);
        Assert.That(state.ExtractedInfo.GlacitePowder, Is.EqualTo(2_345));
    }

    [Test]
    public async Task AlphaReadingsForgetTheBaselinesAndBookNothing()
    {
        var state = new StateObject();
        await ProcessTab(state, DwarvenMinesTab, 0);
        state.ExtractedInfo.OnAlphaNetwork = true;

        await ProcessTab(state, KiwiTab(5_000, 1_000, 100), 1);

        Assert.That(state.ItemsCollectedRecently, Is.Empty);
        Assert.That((state.ExtractedInfo.MithrilPowder, state.ExtractedInfo.GemstonePowder, state.ExtractedInfo.GlacitePowder),
            Is.EqualTo((0, 0, 0)));
        Assert.That(state.ExtractedInfo.PowderProfile, Is.Null);
    }

    [Test]
    public async Task AlphaAtLowerAmountThenMainAtHigherAmountBooksNothing()
    {
        var state = new StateObject();
        state.ExtractedInfo.OnAlphaNetwork = true;
        await ProcessTab(state, BananaTab(100_000, 100_000, 100_000), 0);

        // gaps small enough to pass the hourly cap: only the alpha reset keeps them out
        state.ExtractedInfo.OnAlphaNetwork = false;
        await ProcessTab(state, BananaTab(104_000, 103_000, 102_000), 1);

        Assert.That(state.ItemsCollectedRecently, Is.Empty);
        Assert.That(state.ExtractedInfo.MithrilPowder, Is.EqualTo(104_000));
    }

    [Test]
    public async Task HeartOfTheMountainOnAlphaBooksNothing()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentProfileName = "Banana";
        await ProcessTab(state, BananaTab(1_000, 1_000, 1_000), 0);
        state.ExtractedInfo.OnAlphaNetwork = true;

        await new HeartOfTheListener().Process(HotmArgs(state, 1_500, 1));

        Assert.That(state.ItemsCollectedRecently, Is.Empty);
        Assert.That(state.ExtractedInfo.MithrilPowder, Is.EqualTo(0));
    }

    [Test]
    public async Task HeartOfTheMountainOnMainBooksTheGain()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentProfileName = "Banana";
        await ProcessTab(state, BananaTab(1_000, 1_000, 1_000), 0);

        await new HeartOfTheListener().Process(HotmArgs(state, 1_500, 2));

        Assert.That(state.ItemsCollectedRecently.GetValueOrDefault("MITHRIL_POWDER"), Is.EqualTo(500));
    }

    [Test]
    public async Task JumpAboveTheHourlyCapIsClamped()
    {
        var state = new StateObject();
        await ProcessTab(state, BananaTab(100_000, 100_000, 100_000), 0);

        // +20,000 in 1 minute is 1.2M/h, clamped to 500k/h for a minute (8,333); +5,000 is 300k/h
        await ProcessTab(state, BananaTab(120_000, 105_000, 100_000), 1);

        Assert.That(state.ItemsCollectedRecently, Is.EquivalentTo(new Dictionary<string, int>
        {
            { "MITHRIL_POWDER", 8_333 },
            { "GEMSTONE_POWDER", 5_000 },
        }));
        Assert.That(state.ExtractedInfo.MithrilPowder, Is.EqualTo(120_000));
    }

    [Test]
    public async Task HeartOfTheMountainResetRefundIsClamped()
    {
        var state = new StateObject();
        state.ExtractedInfo.CurrentProfileName = "Banana";
        await ProcessTab(state, BananaTab(50_000, 20_000, 10_000), 0);

        // resetting the HOTM tree refunds the spent powder at once
        await new HeartOfTheListener().Process(HotmArgs(state, 4_250_000, 5));

        // 500k/h for 5 minutes
        Assert.That(state.ItemsCollectedRecently, Is.EquivalentTo(new Dictionary<string, int> { { "MITHRIL_POWDER", 41_666 } }));
        Assert.That(state.ExtractedInfo.MithrilPowder, Is.EqualTo(4_250_000));
    }

    [Test]
    public async Task GainOverAStaleBaselineBooksNothing()
    {
        var state = new StateObject();
        await ProcessTab(state, BananaTab(100_000, 100_000, 100_000), 0);

        await ProcessTab(state, BananaTab(100_500, 100_000, 100_000), 31);

        Assert.That(state.ItemsCollectedRecently, Is.Empty);
        Assert.That(state.ExtractedInfo.MithrilPowder, Is.EqualTo(100_500));
        Assert.That(state.ExtractedInfo.PowderSeenAt, Is.EqualTo(T0.AddMinutes(31)));
    }

    [Test]
    public async Task LegacyStateBooksNothingOnTheFirstReading()
    {
        var state = new StateObject();
        state.ExtractedInfo.MithrilPowder = 100_000; // stored before PowderProfile/PowderSeenAt existed

        await ProcessTab(state, ["Powders:", " Mithril: 100,500"], 0);

        Assert.That(state.ItemsCollectedRecently, Is.Empty);
        Assert.That(state.ExtractedInfo.MithrilPowder, Is.EqualTo(100_500));
    }

    [Test]
    public void LegacyStateWithAKnownProfileBooksNothing()
    {
        var args = ArgsAt(new StateObject(), 0);
        args.currentState.ExtractedInfo.MithrilPowder = 100_000;

        PowderTracker.Track(args, Amounts(PowderTab.MithrilTag, 100_500), "Banana", NullLogger.Instance);

        Assert.That(args.currentState.ItemsCollectedRecently, Is.Empty);
    }

    [Test]
    public async Task SwitchToAProfileWithMorePowderBooksNothing()
    {
        var state = new StateObject();

        await ProcessTab(state, DwarvenMinesTab, 0);
        // small enough increases to pass the hourly cap: only the profile switch keeps them out
        await ProcessTab(state, KiwiTab(1_236_000, 123_500, 12_400), 1);

        Assert.That(state.ItemsCollectedRecently, Is.Empty);
        Assert.That(state.ExtractedInfo.PowderProfile, Is.EqualTo("Kiwi"));
        Assert.That((state.ExtractedInfo.MithrilPowder, state.ExtractedInfo.GemstonePowder, state.ExtractedInfo.GlacitePowder),
            Is.EqualTo((1_236_000, 123_500, 12_400)));
    }

    [Test]
    public async Task NextGainOnTheNewProfileIsBooked()
    {
        var state = new StateObject();

        await ProcessTab(state, DwarvenMinesTab, 0);
        await ProcessTab(state, KiwiTab(5_000_000, 900_000, 80_000), 1);
        await ProcessTab(state, KiwiTab(5_000_400, 900_000, 80_250), 2);

        Assert.That(state.ItemsCollectedRecently, Is.EquivalentTo(new Dictionary<string, int>
        {
            { "MITHRIL_POWDER", 400 },
            { "GLACITE_POWDER", 250 },
        }));
    }

    [Test]
    public async Task ChatProfileSwitchBetweenTabUploadsWithoutProfileLineBooksNothing()
    {
        var state = new StateObject();
        await ProcessTab(state, DwarvenMinesTab, 0);

        await new ProfileSwitchListener().Process(ChatArgs(state,
            "You are playing on profile: Kiwi", "Profile ID: 3f2a8f5e-6a4b-4c1d-9e7f-1a2b3c4d5e6f"));
        await ProcessTab(state, ["Powders:", " Mithril: 1,240,000", " Gemstone: 124,000"], 1);

        Assert.That(state.ItemsCollectedRecently, Is.Empty);
        Assert.That(state.ExtractedInfo.PowderProfile, Is.EqualTo("Kiwi"));
    }

    [Test]
    public void UnknownProfileReadingForgetsTheBaselines()
    {
        var args = ArgsAt(new StateObject(), 0);
        PowderTracker.Track(args, Amounts(PowderTab.GemstoneTag, 1_000), "Banana", NullLogger.Instance);

        PowderTracker.Track(ArgsAt(args.currentState, 1), Amounts(PowderTab.GemstoneTag, 1_200), null, NullLogger.Instance);

        Assert.That(args.currentState.ItemsCollectedRecently, Is.Empty);
        Assert.That(args.currentState.ExtractedInfo.GemstonePowder, Is.EqualTo(1_200));
        Assert.That(args.currentState.ExtractedInfo.PowderProfile, Is.Null);
    }

    [Test]
    public void BananaThenUnknownKiwiReadingThenBananaBooksNothing()
    {
        var state = new StateObject();
        PowderTracker.Track(ArgsAt(state, 0), Amounts(PowderTab.MithrilTag, 100_000), "Banana", NullLogger.Instance);
        // a Kiwi reading whose profile is not known yet
        PowderTracker.Track(ArgsAt(state, 1), Amounts(PowderTab.MithrilTag, 99_000), null, NullLogger.Instance);

        PowderTracker.Track(ArgsAt(state, 2), Amounts(PowderTab.MithrilTag, 100_500), "Banana", NullLogger.Instance);

        Assert.That(state.ItemsCollectedRecently, Is.Empty);
        Assert.That(state.ExtractedInfo.PowderProfile, Is.EqualTo("Banana"));
    }

    [Test]
    public void ProfileNamesCompareCaseInsensitively()
    {
        var state = new StateObject();
        PowderTracker.Track(ArgsAt(state, 0), Amounts(PowderTab.MithrilTag, 100_000), "Banana", NullLogger.Instance);

        PowderTracker.Track(ArgsAt(state, 1), Amounts(PowderTab.MithrilTag, 100_500), "banana", NullLogger.Instance);

        Assert.That(state.ItemsCollectedRecently.GetValueOrDefault("MITHRIL_POWDER"), Is.EqualTo(500));
    }

    [Test]
    public void HeartOfTheMountainReadOnAnotherProfileBooksNothing()
    {
        var args = ArgsAt(new StateObject(), 0);
        args.currentState.ExtractedInfo.CurrentProfileName = "Banana";
        CollectionListener.TrackMithrilPowder(args, 1_000);

        args.currentState.ExtractedInfo.CurrentProfileName = "Kiwi";
        CollectionListener.TrackMithrilPowder(ArgsAt(args.currentState, 1), 2_000);

        Assert.That(args.currentState.ItemsCollectedRecently, Is.Empty);
        Assert.That(args.currentState.ExtractedInfo.MithrilPowder, Is.EqualTo(2_000));
    }

    [Test]
    public void CopyConstructorKeepsEveryPowderBaseline()
    {
        var copy = new ExtractedInfo(PowderInfo());

        AssertPowderFields(copy);
    }

    [Test]
    public void MessagePackRoundTripKeepsEveryPowderBaseline()
    {
        var copy = MessagePackSerializer.Deserialize<ExtractedInfo>(MessagePackSerializer.Serialize(PowderInfo()));

        AssertPowderFields(copy);
    }

    private static ExtractedInfo PowderInfo() => new()
    {
        MithrilPowder = 1,
        GemstonePowder = 2,
        GlacitePowder = 3,
        PowderProfile = "Banana",
        PowderSeenAt = T0,
    };

    private static void AssertPowderFields(ExtractedInfo info)
    {
        Assert.That((info.MithrilPowder, info.GemstonePowder, info.GlacitePowder), Is.EqualTo((1, 2, 3)));
        Assert.That(info.PowderProfile, Is.EqualTo("Banana"));
        Assert.That(info.PowderSeenAt, Is.EqualTo(T0));
    }

    private static string[] BananaTab(int mithril, int gemstone, int glacite) => ProfileTab("Banana", mithril, gemstone, glacite);

    private static string[] KiwiTab(int mithril, int gemstone, int glacite) => ProfileTab("Kiwi", mithril, gemstone, glacite);

    private static string[] ProfileTab(string profile, int mithril, int gemstone, int glacite) =>
    [
        "Profile: " + profile, "Area: Dwarven Mines", "Powders:",
        " Mithril: " + Grouped(mithril), " Gemstone: " + Grouped(gemstone), " Glacite: " + Grouped(glacite),
    ];

    private static string Grouped(int amount) => amount.ToString("N0", CultureInfo.InvariantCulture);

    private static Dictionary<string, int> Amounts(string tag, int amount) => new() { { tag, amount } };

    /// <summary>Runs a tab upload <paramref name="minute"/> minutes after T0 through the listeners in production order.</summary>
    private static async Task ProcessTab(StateObject state, string[] tab, int minute)
    {
        await new ProfileSwitchListener().Process(TabArgs(state, tab, minute));
        await new CollectionListener().Process(TabArgs(state, tab, minute));
    }

    private static MockedUpdateArgs ArgsAt(StateObject state, int minute) => new()
    {
        currentState = state,
        msg = new UpdateMessage { ReceivedAt = T0.AddMinutes(minute) }
    };

    private static MockedUpdateArgs TabArgs(StateObject state, string[] tab, int minute) => new()
    {
        currentState = state,
        msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.Tab, Tab = tab, ReceivedAt = T0.AddMinutes(minute) }
    };

    private static MockedUpdateArgs HotmArgs(StateObject state, int mithril, int minute) => new()
    {
        currentState = state,
        msg = new UpdateMessage
        {
            Kind = UpdateMessage.UpdateKind.INVENTORY,
            ReceivedAt = T0.AddMinutes(minute),
            Chest = new ChestView
            {
                Name = "Heart of the Mountain",
                Items = [new() { ItemName = "§2Mithril Powder", Description = $"§7You have §a{Grouped(mithril)} Mithril Powder" }]
            }
        }
    };

    private static MockedUpdateArgs ChatArgs(StateObject state, params string[] lines) => new()
    {
        currentState = state,
        msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.CHAT, ChatBatch = new List<string>(lines) }
    };
}
