using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Controllers;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class MenuSamplerListenerTests
{
    private class FakeStore : IMenuSampleStore
    {
        public readonly Dictionary<string, List<StoredMenuSample>> Samples = new();
        public int AddCalls;
        public long Dropped;
        public Task<MenuAddResult> Add(string name, string playerKey, MenuSample sample, int maxNames, int maxPlayers)
        {
            AddCalls++;
            if (!Samples.ContainsKey(name) && Samples.Count >= maxNames)
            {
                Dropped++;
                return Task.FromResult(new MenuAddResult(MenuAddOutcome.NameCapReached, 0));
            }
            var merged = RedisMenuSampleStore.Merge(Samples.GetValueOrDefault(name) ?? [], playerKey, sample, maxPlayers);
            Samples[name] = merged;
            return Task.FromResult(new MenuAddResult(MenuAddOutcome.Stored, merged.Count));
        }
        public Task<List<MenuSample>> Get(string name) => Task.FromResult(Samples.GetValueOrDefault(name)?.Select(s => s.Sample).ToList() ?? []);
        public Task<Dictionary<string, List<MenuSample>>> GetMany(IReadOnlyCollection<string> names)
            => Task.FromResult(names.Where(Samples.ContainsKey).ToDictionary(n => n, n => Samples[n].Select(s => s.Sample).ToList()));
        public Task<List<MenuNameInfo>> ListNames()
            => Task.FromResult(Samples.Select(s => new MenuNameInfo { Name = s.Key, Samples = s.Value.Count, LastSampledAt = s.Value.Max(v => v.Sample.SampledAt) })
                .OrderByDescending(n => n.LastSampledAt).ToList());
        public Task<long> GetDroppedNameCount() => Task.FromResult(Dropped);
    }

    private const string LoadoutLoreText = "§7Loadout 2\n§7Helmet: §aFig Cap\n§7Chestplate: §aFigmail\n§7Leggings: §aFig Trousers\n§7Boots: §aFig Striders\n\n§7Necklace: §9Peony Necklace\n§7Cloak: §5David's Cloak\n§7Belt: §5Ender Belt\n§7Gloves/Bracelet: §5Ender Gauntlet\n\n§7Pet: §cNone\n§7HOTM: §cNone\n§7HOTF: §cNone\n§7Power Stone: §cNone\n§7Tuning Template Slot: §cNone\n§7Favored Bait: §cNone";

    private static DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private static MockedUpdateArgs Args(string chestName, IMenuSampleStore store, string player = "p1", int menuSlots = 54, int inventorySlots = 36)
    {
        var items = Enumerable.Range(0, menuSlots).Select(i => new Item { Tag = $"MENU_{i}", ItemName = $"§aItem {i}", Description = $"lore {i}" })
            .Concat(Enumerable.Range(0, inventorySlots).Select(i => new Item { Tag = $"INV_{i}", ItemName = "inv", Description = "inventory lore" }))
            .ToList();
        if (menuSlots > 3)
            items[3] = new Item { Tag = null, ItemName = null, Description = null }; // empty glass pane slot without data
        if (menuSlots > 4)
            items[4] = new Item { Tag = null, ItemName = "§cClose", Description = "lore 4" }; // every SkyBlock menu has tagless buttons
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject { PlayerId = player },
            msg = new UpdateMessage { ReceivedAt = T0, PlayerId = player, Chest = new ChestView { Name = chestName, Items = items } }
        };
        args.AddService<IMenuSampleStore>(store);
        return args;
    }

    private static MenuSamplerListener Listener(Func<DateTime>? clock = null, Action<MenuSamplerOptions>? configure = null)
    {
        var options = new MenuSamplerOptions();
        configure?.Invoke(options);
        return new MenuSamplerListener(options, clock ?? (() => T0));
    }

    // ── normalization ──

    [TestCase("(1/3) Loadouts", "(1/#) Loadouts")]
    [TestCase("(2/2) Loadouts", "(2/#) Loadouts")]
    [TestCase("(5/9) Loadouts", "(5/#) Loadouts")]
    [TestCase("(12/20) Loadouts", "(#/#) Loadouts")]
    [TestCase("§aCatacombs Gate", "Catacombs Gate")]
    [TestCase("Backpack (Slot 12)", "Backpack (Slot #)")]
    [TestCase("Ender Chest (4/9)", "Ender Chest (4/#)")]
    [TestCase("  Your   Skills ", "Your Skills")]
    [TestCase("Auctions: \"hyperion\"", "Auctions: \"*\"")]
    [TestCase("Auctions: \"Aspect of the End 2\"", "Auctions: \"*\"")]
    [TestCase("Bazaar ➜ Enchanted Diamond", "Bazaar ➜ *")]
    [TestCase("Enchanted Diamond ➜ Instant Buy", "* ➜ Instant Buy")]
    [TestCase("You                  Steve_123", "Trade")]
    [TestCase("§aYou          Alex", "Trade")]
    [TestCase("Steve_123's Profile", "<player>'s Profile")]
    [TestCase("Shen's Auction", "Shen's Auction")]
    [TestCase("Collection - 1,234.5 coins", "Collection - # coins")]
    [TestCase("Catacombs - Floor VII", "Catacombs - Floor VII")]
    [TestCase("", "")]
    [TestCase(null, "")]
    public void TitlesAreNormalized(string? title, string expected)
    {
        MenuTitleNormalizer.Normalize(title).Should().Be(expected);
    }

    [TestCase("(2/2) Catacombs (M7) RNG Meter", "(2/#) Catacombs (M7) RNG Meter")]
    [TestCase("(2/#) Catacombs (M7) RNG Meter", "(2/#) Catacombs (M7) RNG Meter")]
    [TestCase("(12/#) Loadouts", "(#/#) Loadouts")]
    [TestCase("Ender Chest (4/#)", "Ender Chest (4/#)")]
    public void Normalize_IsIdempotentOnItsOwnOutput(string title, string expected)
    {
        var once = MenuTitleNormalizer.Normalize(title);
        once.Should().Be(expected);
        MenuTitleNormalizer.Normalize(once).Should().Be(once);
    }

    [Test]
    public async Task GetSamples_ReadsAStoredPagedNameAsGiven()
    {
        var store = new Mock<IMenuSampleStore>();
        var sample = new MenuSample { Title = "(2/#) Catacombs (M7) RNG Meter" };
        store.Setup(s => s.Get("(2/#) Catacombs (M7) RNG Meter")).ReturnsAsync([sample]);
        store.Setup(s => s.Get("(#/#) Catacombs (M7) RNG Meter")).ReturnsAsync([]);

        var result = await new MenuSampleController(store.Object).GetSamples("(2/#) Catacombs (M7) RNG Meter");

        result.Value.Should().ContainSingle().Which.Should().BeSameAs(sample);
    }

    [Test]
    public async Task GetSamples_FallsBackToTheNormalizedRawTitle()
    {
        var store = new Mock<IMenuSampleStore>();
        var sample = new MenuSample { Title = "(2/#) Loadouts" };
        store.Setup(s => s.Get(It.IsAny<string>())).ReturnsAsync([]);
        store.Setup(s => s.Get("(2/#) Loadouts")).ReturnsAsync([sample]);

        var result = await new MenuSampleController(store.Object).GetSamples("(2/3) Loadouts");

        result.Value.Should().ContainSingle();
    }

    // real title shapes from the production names list (player names invented)
    [TestCase("Shards ➜ Torrid Shard", "Shards ➜ *")]
    [TestCase("Shards ➜ Giant Isopod Shard", "Shards ➜ *")]
    [TestCase("Enchant Item ➜ Sharpness VII", "Enchant Item ➜ *")]
    [TestCase("Reforge Stones ➜ Moil", "Reforge Stones ➜ *")]
    [TestCase("Mining ➜ Shards", "Mining ➜ *")]
    [TestCase("(2/4) Museum ➜ Farming", "(2/#) Museum ➜ *")]
    [TestCase("Catacombs Misc. ➜ Wither Cataly", "Catacombs Misc. ➜ *")]
    [TestCase("Fishing Minion IX", "* Minion #")]
    [TestCase("Coal Minion VII", "* Minion #")]
    [TestCase("Mangrove Log VI Rewards", "* # Rewards")]
    [TestCase("Redstone Dust IX Rewards", "* # Rewards")]
    [TestCase("The Professor III Rewards", "* # Rewards")]
    [TestCase("Vampire Slayer LVL Rewards", "* Slayer LVL Rewards")]
    [TestCase("Mithril Collection", "* Collection")]
    [TestCase("Mangrove Log Collection", "* Collection")]
    [TestCase("Slayer Recipes", "* Recipes")]
    [TestCase("(1/2) Voidgloom Seraph Recipes", "(1/#) * Recipes")]
    [TestCase("Cobblestone Minion Recipes", "* Minion Recipes")]
    [TestCase("Large Enchanted Fishing Sack", "* Sack")]
    [TestCase("Witch's Sack", "* Sack")]
    [TestCase("Abiphone XIII", "Abiphone #")]
    [TestCase("Abiphone XIII Jade", "Abiphone # *")]
    [TestCase("(2/3) Abiphone XIV Black", "(2/#) Abiphone # *")]
    [TestCase("Abiphone Shop", "Abiphone Shop")]
    [TestCase("Abiphone Flip+", "Abiphone Flip+")]
    [TestCase("Visit portal_hub", "Visit *")]
    [TestCase("Profile: Cucumber", "Profile: *")]
    [TestCase("Profile: Cucumber (Co-op)", "Profile: * (Co-op)")]
    [TestCase("Akhil_5's Profile [GUEST]", "<player>'s Profile [GUEST]")]
    [TestCase("_I_Shadow_I_'s Profile [GUEST]", "<player>'s Profile [GUEST]")]
    [TestCase("Bobby' Profile [GUEST]", "<player>'s Profile [GUEST]")]
    [TestCase("AsianPampers' Profile [GUEST]", "<player>'s Profile [GUEST]")]
    [TestCase("Catacombs - Floor VII", "Catacombs - Floor VII")]
    [TestCase("Master Catacombs - Floor III", "Master Catacombs - Floor III")]
    [TestCase("Catacombs (M4) RNG Meter", "Catacombs (M4) RNG Meter")]
    [TestCase("(1/2) Catacombs (M7) RNG Meter", "(1/#) Catacombs (M7) RNG Meter")]
    [TestCase("(2/2) Catacombs (M7) RNG Meter", "(2/#) Catacombs (M7) RNG Meter")]
    [TestCase("Catacombs (F7) RNG Meter", "Catacombs (F7) RNG Meter")]
    [TestCase("Auctions: \"Final Destination He", "Auctions: \"*\"")]
    [TestCase("Catacombs Gate", "Catacombs Gate")]
    [TestCase("Agatha's Shop", "Agatha's Shop")]
    public void ProductionTitlesAreNormalized(string title, string expected)
    {
        MenuTitleNormalizer.Normalize(title).Should().Be(expected);
    }

    [Test]
    public void DifferentFloorsStayDistinct_DifferentGuestsAndMinionsCollapse()
    {
        new[] { "Catacombs (M4) RNG Meter", "Catacombs (M7) RNG Meter", "Catacombs (F7) RNG Meter" }
            .Select(MenuTitleNormalizer.Normalize).Distinct().Should().HaveCount(3);
        new[] { "Master Catacombs - Floor III", "Master Catacombs - Floor IV" }
            .Select(MenuTitleNormalizer.Normalize).Distinct().Should().HaveCount(2);
        new[] { "Anna's Profile [GUEST]", "Bob_7' Profile [GUEST]" }.Select(MenuTitleNormalizer.Normalize).Distinct().Should().ContainSingle();
        new[] { "Cow Minion VI", "Cow Minion IX", "Iron Minion XI" }.Select(MenuTitleNormalizer.Normalize).Distinct().Should().ContainSingle();
    }

    [Test]
    public void LongTitle_IsCut()
    {
        MenuTitleNormalizer.Normalize(new string('x', 500)).Length.Should().Be(MenuTitleNormalizer.MaxLength);
    }

    [Test]
    public void DifferentPlayersAndSearchTerms_CollapseToOneName()
    {
        new[] { "You          Alice", "You                  Bob" }.Select(MenuTitleNormalizer.Normalize).Distinct().Should().ContainSingle();
        new[] { "Auctions: \"a\"", "Auctions: \"b c\"" }.Select(MenuTitleNormalizer.Normalize).Distinct().Should().ContainSingle();
    }

    // ── sampling ──

    [Test]
    public async Task AnyChest_IsSampled_UnderItsNormalizedName_WithMenuSlotsOnly()
    {
        var store = new FakeStore();

        await Listener().Process(Args("§a(2/3) Loadouts", store));

        var sample = store.Samples["(2/#) Loadouts"].Single().Sample;
        sample.Title.Should().Be("(2/#) Loadouts");
        sample.SampledAt.Should().Be(T0);
        sample.TotalSlots.Should().Be(90);
        sample.Items.Should().HaveCount(53, "54 menu slots minus the empty one");
        sample.Items.Should().OnlyContain(i => i.Slot < 54 && (i.Tag == null || i.Tag.StartsWith("MENU_")), "the player's inventory is not menu content");
        sample.Items.Single(i => i.Slot == 5).Should().BeEquivalentTo(new MenuSampleItem { Slot = 5, Tag = "MENU_5", Name = "§aItem 5", Description = "lore 5" });
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task EmptyTitle_IsIgnored(string title)
    {
        var store = new FakeStore();

        await Listener().Process(Args(title, store));

        store.AddCalls.Should().Be(0);
    }

    [Test]
    public async Task NoChest_IsIgnored()
    {
        var store = new FakeStore();
        var args = Args("Catacombs Gate", store);
        args.msg.Chest = null;

        await Listener().Process(args);

        store.AddCalls.Should().Be(0);
    }

    [Test]
    public async Task SamplesOfThreeDifferentPlayersAreKept_AFourthReplacesTheOldest()
    {
        var store = new FakeStore();
        var now = T0;
        var listener = Listener(() => now);
        foreach (var player in new[] { "a", "b", "c", "d" })
        {
            var args = Args("Your Skills", store, player);
            args.msg.ReceivedAt = now;
            await listener.Process(args);
            // the first three fill the name, the fourth arrives after the resample interval (a full name is not touched earlier)
            now = player == "c" ? now.AddHours(7) : now.AddMinutes(1);
        }

        var stored = store.Samples["Your Skills"];
        stored.Should().HaveCount(3);
        stored.Select(s => s.PlayerKey).Should().BeEquivalentTo(new[] { "b", "c", "d" }.Select(MenuSamplerListener.HashPlayer), "a was the oldest");
        stored.Select(s => s.Sample.SampledAt).Should().BeInDescendingOrder("newest first");
    }

    [Test]
    public void SamePlayerReplacesItsOwnSample_NeverHoldsTwo()
    {
        var older = new MenuSample { SampledAt = T0 };
        var newer = new MenuSample { SampledAt = T0.AddHours(1) };
        var existing = new List<StoredMenuSample> { new() { PlayerKey = "a", Sample = older }, new() { PlayerKey = "b", Sample = older } };

        var merged = RedisMenuSampleStore.Merge(existing, "a", newer, 3);

        merged.Should().HaveCount(2);
        merged.Single(m => m.PlayerKey == "a").Sample.Should().BeSameAs(newer);
        merged[0].PlayerKey.Should().Be("a", "newest first");
    }

    [Test]
    public async Task PlayerIdentityIsHashed_AndNeverPartOfWhatTheApiReturns()
    {
        var store = new FakeStore();
        await Listener().Process(Args("Your Skills", store, "SecretPlayerName"));

        store.Samples["Your Skills"].Single().PlayerKey.Should().Be(MenuSamplerListener.HashPlayer("SecretPlayerName")).And.NotContain("Secret");
        var controller = new MenuSampleController(store);
        var samples = (await controller.GetSamples("Your Skills")).Value!;
        var search = (await controller.Search("lore")).Value!;
        var json = JsonConvert.SerializeObject(new object[] { samples, search, await controller.GetNames() });
        json.Should().NotContain("SecretPlayerName").And.NotContain(MenuSamplerListener.HashPlayer("SecretPlayerName")).And.NotContainEquivalentOf("PlayerKey");
        typeof(MenuSample).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Player"));
    }

    [TestCase("Chest")]
    [TestCase("Large Chest")]
    [TestCase("Ender Chest (3/9)")]
    [TestCase("Backpack (Slot 4)")]
    [TestCase("§aBackpack (Slot 4)")]
    [TestCase("You                  Alex")]
    public async Task DeniedTitles_AreNotSampled(string title)
    {
        var store = new FakeStore();

        await Listener().Process(Args(title, store));

        store.AddCalls.Should().Be(0);
    }

    private static Item Button(string name) => new() { Tag = null, ItemName = name, Description = "" };
    private static Item Pane() => new() { Tag = null, ItemName = " ", Description = "" };
    private static Item Drop(string tag) => new() { Tag = tag, ItemName = tag, Description = "drop" };

    private static MockedUpdateArgs ViewArgs(string title, IMenuSampleStore store, List<Item> menu)
    {
        var items = menu.Concat(Enumerable.Range(0, 36).Select(i => Drop($"INV_{i}"))).ToList();
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject { PlayerId = "p1" },
            msg = new UpdateMessage { ReceivedAt = T0, PlayerId = "p1", Chest = new ChestView { Name = title, Items = items } }
        };
        args.AddService<IMenuSampleStore>(store);
        return args;
    }

    /// <summary>Shape of the Catacombs RNG meter sample: tagged drops, named tagless buttons at 4 and 48+.</summary>
    private static List<Item> RngMeterLike()
    {
        var menu = Enumerable.Range(0, 54).Select(_ => Pane()).ToList();
        menu[4] = Button("Catacombs (M7) RNG Meter");
        menu[10] = Drop("PRECURSOR_GEAR");
        menu[43] = Drop("IMPLOSION_SCROLL");
        menu[48] = Button("Go Back");
        menu[49] = Button("Close");
        return menu;
    }

    [Test]
    public async Task MenuWithTaggedDropsAndTaglessButtons_IsSampled()
    {
        var store = new FakeStore();

        await Listener().Process(ViewArgs("(2/2) Catacombs (M7) RNG Meter", store, RngMeterLike()));

        store.Samples.Keys.Should().BeEquivalentTo("(2/#) Catacombs (M7) RNG Meter");
    }

    [TestCase("Большой сундук")]
    [TestCase("Coffre")]
    public async Task PlainLocalizedContainer_WithoutButtons_IsNotSampled(string title)
    {
        var store = new FakeStore();
        var menu = Enumerable.Range(0, 54).Select(i => i % 3 == 0 ? Drop("ENCHANTED_COAL") : Pane()).ToList();

        await Listener().Process(ViewArgs(title, store, menu));

        store.AddCalls.Should().Be(0);
    }

    [Test]
    public async Task ButtonsOnlyInThePlayersInventoryPart_DoNotMakeAMenu()
    {
        var store = new FakeStore();
        var menu = Enumerable.Range(0, 54).Select(_ => Pane()).ToList();
        var args = ViewArgs("Kiste", store, menu);
        args.msg.Chest!.Items[60] = Button("Close");

        await Listener().Process(args);

        store.AddCalls.Should().Be(0);
    }

    [Test]
    public async Task BazaarProductPage_TitledLikeTheItem_IsStoredUnderOneName()
    {
        var store = new FakeStore();
        foreach (var product in new[] { "Diamond", "Enchanted Coal" })
        {
            var menu = Enumerable.Range(0, 54).Select(_ => Pane()).ToList();
            menu[10] = Button("§aBuy Instantly");
            menu[11] = Button("§6Create Buy Order");
            menu[49] = Button("Close");
            await Listener().Process(ViewArgs(product, store, menu));
        }

        store.Samples.Keys.Should().BeEquivalentTo(MenuSamplerListener.BazaarProductName);
    }

    [Test]
    public async Task RecipePage_TitledLikeTheItem_IsStoredUnderOneName()
    {
        var store = new FakeStore();
        foreach (var product in new[] { "Magnetic Talisman", "Aspect of the Void" })
        {
            var menu = Enumerable.Range(0, 54).Select(_ => Pane()).ToList();
            menu[25] = Drop("RESULT");
            menu[32] = Button("§aSupercraft");
            menu[49] = Button("Close");
            await Listener().Process(ViewArgs(product, store, menu));
        }

        store.Samples.Keys.Should().BeEquivalentTo(MenuSamplerListener.RecipeName);
    }

    [Test]
    public async Task DenyList_IsConfigurable()
    {
        var store = new FakeStore();
        var listener = Listener(configure: o => o.Deny = [.. MenuSamplerOptions.DefaultDeny, new Regex("^Skyblock Menu$")]);

        await listener.Process(Args("SkyBlock Menu", store)); // different case: not denied
        await listener.Process(Args("Skyblock Menu", store));

        store.Samples.Keys.Should().BeEquivalentTo("SkyBlock Menu");
    }

    [Test]
    public async Task NameCap_DropsNewNames_CountsThem_AndKeepsExistingOnes()
    {
        var store = new FakeStore();
        var listener = Listener(configure: o => o.MaxNames = 2);

        await listener.Process(Args("Menu One", store));
        await listener.Process(Args("Menu Two", store));
        await listener.Process(Args("Menu Three", store));
        await listener.Process(Args("Menu Three", store, "other"));

        store.Samples.Keys.Should().BeEquivalentTo("Menu One", "Menu Two");
        listener.DroppedNames.Should().Be(1);
        store.AddCalls.Should().Be(3, "a dropped name is not retried on the next open");
    }

    [Test]
    public async Task DroppedName_IsRetriedAfterTheRetryInterval()
    {
        var store = new FakeStore();
        var now = T0;
        var listener = Listener(() => now, o => { o.MaxNames = 1; o.DroppedRetry = TimeSpan.FromHours(1); });
        await listener.Process(Args("Menu One", store));
        await listener.Process(Args("Menu Two", store));
        store.Samples.Remove("Menu One");
        now = now.AddMinutes(61);

        await listener.Process(Args("Menu Two", store));

        store.Samples.Keys.Should().BeEquivalentTo("Menu Two");
    }

    [Test]
    public async Task FullName_IsSkippedWithoutAStoreCall_UntilTheResampleInterval()
    {
        var store = new FakeStore();
        var now = T0;
        var listener = Listener(() => now);
        foreach (var player in new[] { "a", "b", "c" })
            await listener.Process(Args("Your Skills", store, player));
        store.AddCalls.Should().Be(3);

        for (var i = 0; i < 50; i++)
            await listener.Process(Args("Your Skills", store, i % 2 == 0 ? "a" : "z"));
        store.AddCalls.Should().Be(3, "all three players are known, nothing is read or written");

        now = now.AddHours(6).AddMinutes(1);
        await listener.Process(Args("Your Skills", store, "z"));
        store.AddCalls.Should().Be(4, "renewed once the interval passed");
        await listener.Process(Args("Your Skills", store, "y"));
        store.AddCalls.Should().Be(4);
    }

    [Test]
    public async Task NotYetFullName_SamplesNewPlayers_ButNotTheSamePlayerAgain()
    {
        var store = new FakeStore();
        var listener = Listener();

        await listener.Process(Args("Your Skills", store, "a"));
        await listener.Process(Args("Your Skills", store, "a"));
        await listener.Process(Args("Your Skills", store, "b"));

        store.AddCalls.Should().Be(2);
    }

    [Test]
    public async Task ThrowingStore_DoesNotPropagate_AndBacksOff()
    {
        var store = new Mock<IMenuSampleStore>();
        store.Setup(s => s.Add(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MenuSample>(), It.IsAny<int>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));
        var listener = Listener();

        var act = async () =>
        {
            await listener.Process(Args("Catacombs Gate", store.Object));
            await listener.Process(Args("Your Skills", store.Object));
        };

        await act.Should().NotThrowAsync();
        store.Verify(s => s.Add(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<MenuSample>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once, "backoff after a failure");
    }

    [Test]
    public async Task MissingStoreService_DoesNotPropagate()
    {
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject { PlayerId = "p" },
            msg = new UpdateMessage { Chest = new ChestView { Name = "Catacombs Gate", Items = Args("x", new FakeStore()).msg.Chest!.Items } }
        };

        var act = () => Listener().Process(args);

        await act.Should().NotThrowAsync();
    }

    [Test]
    public async Task NoPlayerIdentity_IsNotSampled()
    {
        var store = new FakeStore();
        var args = Args("Your Skills", store, "");
        args.msg.PlayerId = "";

        await Listener().Process(args);

        store.AddCalls.Should().Be(0);
    }

    [Test]
    public void Sampler_IsOptional_SoItNeverStopsTheStateSave()
    {
        Listener().Optional.Should().BeTrue();
    }

    [Test]
    public void LongDescriptions_AreCut()
    {
        var chest = Args("Catacombs Gate", new FakeStore()).msg.Chest!;
        chest.Items[0].Description = new string('x', 10_000);

        MenuSamplerListener.BuildSample(chest, "n", DateTime.UtcNow, new MenuSamplerOptions()).Items.First(i => i.Slot == 0).Description!.Length.Should().Be(2000);
    }

    [Test]
    public void SlotsPerSample_AreCapped()
    {
        var chest = Args("Big", new FakeStore(), menuSlots: 54).msg.Chest!;

        MenuSamplerListener.BuildSample(chest, "n", DateTime.UtcNow, new MenuSamplerOptions { MaxSlots = 10 }).Items.Should().OnlyContain(i => i.Slot < 10);
    }

    [Test]
    public void IndexEncoding_Roundtrips()
    {
        var info = RedisMenuSampleStore.DecodeIndex("n", RedisMenuSampleStore.EncodeIndex(3, T0));

        info.Should().BeEquivalentTo(new MenuNameInfo { Name = "n", Samples = 3, LastSampledAt = T0 });
    }

    // ── retrieval and search ──

    private static async Task<(FakeStore, MenuSampleController)> Seeded()
    {
        var store = new FakeStore();
        var listener = Listener();
        var a = Args("Your Skills", store, "a");
        a.msg.Chest!.Items[7] = new Item { Tag = "SKILL_COMBAT", ItemName = "§aCombat XXVIII", Description = "§7Fight mobs and §cspecial bosses\n§7to earn XP" };
        await listener.Process(a);
        await listener.Process(Args("(1/3) Loadouts", store, "a"));
        await listener.Process(Args("Bazaar ➜ Coal", store, "a"));
        return (store, new MenuSampleController(store));
    }

    [Test]
    public async Task ListNames_PagesAndFiltersByName()
    {
        var (_, controller) = await Seeded();

        var all = await controller.GetNames(pageSize: 2);
        all.Total.Should().Be(3);
        all.Items.Should().HaveCount(2);
        (await controller.GetNames(pageSize: 2, page: 1)).Items.Should().HaveCount(1);
        var filtered = await controller.GetNames("LOADOUT");
        filtered.Items.Select(i => i.Name).Should().Equal("(1/#) Loadouts");
        filtered.Items[0].Samples.Should().Be(1);
    }

    [Test]
    public async Task GetSamples_NormalizesTheRequestedName_AndIs404ForUnknown()
    {
        var (_, controller) = await Seeded();

        (await controller.GetSamples("(1/9) Loadouts")).Value.Should().HaveCount(1);
        (await controller.GetSamples("Nothing")).Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.NotFoundObjectResult>();
    }

    [TestCase("combat xxviii", "itemName")]
    [TestCase("skill_combat", "tag")]
    [TestCase("SPECIAL BOSSES", "description")]
    public async Task Search_MatchesItemNameTagAndDescription_CaseInsensitive_IgnoringColours(string query, string field)
    {
        var (_, controller) = await Seeded();

        var result = (await controller.Search(query)).Value!;

        var hit = result.Results.Should().ContainSingle().Subject;
        hit.Name.Should().Be("Your Skills");
        hit.Matches.Should().ContainSingle().Which.MatchedIn.Should().Contain(field);
        hit.Matches[0].Slot.Should().Be(7);
        hit.Matches[0].SampleIndex.Should().Be(0);
    }

    [Test]
    public async Task Search_MatchesTheChestName()
    {
        var (_, controller) = await Seeded();

        var result = (await controller.Search("loadouts")).Value!;

        result.Results.Should().ContainSingle(r => r.Name == "(1/#) Loadouts" && r.NameMatch);
    }

    [Test]
    public async Task Search_IsBounded_ByResultLimit_AndRejectsShortQueries()
    {
        var (_, controller) = await Seeded();

        var result = (await controller.Search("lore", limit: 2)).Value!;

        result.Results.Should().HaveCount(2);
        result.Truncated.Should().BeTrue();
        result.Results.All(r => r.Matches.Count <= MenuSampleSearch.MaxMatchesPerName).Should().BeTrue();
        (await controller.Search("a")).Result.Should().BeOfType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>();
    }

    // ── Loadouts lore parser ──

    [Test]
    public void LoadoutLore_ParsesWornPieces_StrippingColorCodes()
    {
        var lore = LoadoutLore.Parse(LoadoutLoreText)!;

        lore.Name.Should().Be("Loadout 2");
        lore.Pieces.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["Helmet"] = "Fig Cap", ["Chestplate"] = "Figmail", ["Leggings"] = "Fig Trousers", ["Boots"] = "Fig Striders",
            ["Necklace"] = "Peony Necklace", ["Cloak"] = "David's Cloak", ["Belt"] = "Ender Belt", ["Gloves/Bracelet"] = "Ender Gauntlet"
        });
    }

    [Test]
    public void LoadoutLore_NoneIsEmptySlot_AndOtherLinesAreIgnored()
    {
        var lore = LoadoutLore.Parse("Loadout 1\nHelmet: None\nChestplate: Figmail\nPet: None\nHOTM: None")!;

        lore.Pieces["Helmet"].Should().BeNull();
        lore.Pieces["Chestplate"].Should().Be("Figmail");
        lore.Pieces.Keys.Should().BeEquivalentTo("Helmet", "Chestplate");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("Click to select!\nPet: None")]
    public void LoadoutLore_WithoutWornSlots_IsNull(string? text)
    {
        LoadoutLore.Parse(text).Should().BeNull();
    }
}
