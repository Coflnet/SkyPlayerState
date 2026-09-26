using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Cassandra;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.PlayerName.Client.Api;
using Coflnet.Sky.PlayerState.Controllers;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;
using StackExchange.Redis;

namespace Coflnet.Sky.PlayerState.Bazaar;

// Explicit because the ordinary Docker build runs tests before disposable storage exists.
[TestFixture, Explicit("Run bash tests/run-bazaar-achievement-storage.sh"), NonParallelizable]
public class BazaarAchievementStorageTests
{
    private const string Name = "BazaarTester";
    private static readonly Guid Player = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid Other = Guid.Parse("aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee");
    private Cluster cluster = null!;
    private ISession session = null!;
    private ConnectionMultiplexer redis = null!;
    private readonly Mock<ICassandraService> cassandra = new();
    private readonly Mock<IPlayerNameApi> names = new();

    [SetUp]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("BAZAAR_STORAGE_TEST")
            ?? throw new InvalidOperationException("Disposable storage is required");
        cluster = Cluster.Builder().AddContactPoint(host).Build();
        session = await cluster.ConnectAsync();
        await session.ExecuteAsync(new SimpleStatement("DROP KEYSPACE IF EXISTS bazaar_achievement_test"));
        await session.ExecuteAsync(new SimpleStatement("CREATE KEYSPACE bazaar_achievement_test WITH replication = {'class':'SimpleStrategy','replication_factor':1}"));
        session.ChangeKeyspace("bazaar_achievement_test");
        redis = await ConnectionMultiplexer.ConnectAsync(Environment.GetEnvironmentVariable("BAZAAR_REDIS_TEST") + ":6379");
        await redis.GetDatabase().KeyDeleteAsync("pstate:" + Name);
        cassandra.Setup(c => c.GetSession()).ReturnsAsync(session);
        names.Setup(n => n.PlayerNameUuidNameGetAsync(Name, 0, default)).ReturnsAsync(Player.ToString("N"));
        names.Setup(n => n.PlayerNameNameUuidGetAsync(Player.ToString("N"), 0, default)).ReturnsAsync("\"" + Name + "\"");
        names.Setup(n => n.PlayerNameNameUuidGetAsync(Other.ToString("N"), 0, default)).ReturnsAsync("OtherTrader");
    }

    [TearDown]
    public void Cleanup()
    {
        redis?.Dispose();
        session?.Dispose();
        cluster?.Dispose();
    }

    private PersistenceService Persistence() => new(cassandra.Object, NullLogger<PersistenceService>.Instance, redis);
    private BazaarProfitTracker Tracker() => new(session, NullLogger<BazaarProfitTracker>.Instance);

    private async Task Process(StateObject state, params string[] messages)
    {
        var items = new Mock<IItemsApi>();
        items.Setup(i => i.ItemsSearchTermIdGetAsync(It.IsAny<string>(), 0, default)).ReturnsAsync(5);
        items.Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync(new List<Items.Client.Model.SearchResult>
            {
                new() { Tag = "COAL", Flags = Items.Client.Model.ItemFlags.BAZAAR }
            });
        using var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage
            {
                PlayerId = Name, UserId = "synthetic-user", Kind = UpdateMessage.UpdateKind.CHAT,
                ReceivedAt = DateTime.UtcNow, ChatBatch = messages.ToList()
            }
        };
        args.AddService<IPlayerNameApi>(names.Object);
        args.AddService<IItemsApi>(items.Object);
        args.AddService<ITransactionService>(Mock.Of<ITransactionService>());
        args.AddService<IBazaarProfitTracker>(Tracker());
        args.AddService<IAchievementService>(new AchievementService());
        args.AddService<ILogger<BazaarOrderListener>>(NullLogger<BazaarOrderListener>.Instance);
        await new ProfileAndNameUpdate().Process(args);
        await new BazaarOrderListener().Process(args);
        await Persistence().SaveStateObject(state);
        await Persistence().ForceSave(state);
    }

    private const string Buy = "[Bazaar] Claimed 64x Coal worth 134.4 coins bought for 2.1 each!";
    private const string Sell = "[Bazaar] Claimed 303.7 coins from selling 64x Coal at 4.8 each!";

    [TestCase(false), TestCase(true)]
    public async Task ProfitableClaimsAreVisibleToUncachedUuidConsumer(bool sameBatch)
    {
        var state = await Persistence().GetStateObject(Name);
        if (sameBatch)
            await Process(state, "[Bazaar] Claiming order...", Buy, "[Bazaar] Claiming order...", Sell);
        else
        {
            await Process(state, "[Bazaar] Claiming order...", Buy);
            await redis.GetDatabase().KeyDeleteAsync("pstate:" + Name);
            state = await Persistence().GetStateObject(Name);
            await Process(state, "[Bazaar] Claiming order...", Sell);
        }
        var flips = await Tracker().GetFlips(Player);
        Assert.That(flips, Has.Count.EqualTo(1), "Real tracker must persist the completed round trip");
        Assert.Multiple(() =>
        {
            Assert.That(flips[0].Amount, Is.EqualTo(64));
            Assert.That(flips[0].BuyPrice, Is.EqualTo(1344));
            Assert.That(flips[0].SellPrice, Is.EqualTo(3037));
            Assert.That(flips[0].Profit, Is.EqualTo(1693));
        });
        await redis.GetDatabase().KeyDeleteAsync("pstate:" + Name);
        Assert.That((await Persistence().GetStateObject(Name)).UnlockedAchievements,
            Does.Contain(Achievement.BazaarFlipProfit), "Award must survive Cassandra reload");
        await AssertConsumer(Player, true);
        await AssertConsumer(Other, false);
    }

    [Test]
    public async Task ExistingNameStateKeepsEarnedAchievement()
    {
        var state = new StateObject { PlayerId = Name, McInfo = new McInfo { Name = Name, Uuid = Player } };
        state.UnlockedAchievements.Add(Achievement.BazaarFlipProfit);
        await Persistence().ForceSave(state);
        await AssertConsumer(Player, true);
        await AssertConsumer(Other, false);
        Assert.That((await Persistence().GetStateObject(Name)).PlayerId, Is.EqualTo(Name));
    }

    [Test]
    public async Task NameReuseCannotGrantAnotherPlayersAchievement()
    {
        var previousOwner = new StateObject { PlayerId = Name, McInfo = new McInfo { Name = Name, Uuid = Other } };
        previousOwner.UnlockedAchievements.Add(Achievement.BazaarFlipProfit);
        await Persistence().ForceSave(previousOwner);
        await AssertConsumer(Player, false);
    }

    [Test]
    public async Task UuidKeyedAchievementsRemainAvailable()
    {
        var state = new StateObject { PlayerId = Player.ToString("N"), McInfo = new McInfo { Uuid = Player } };
        state.UnlockedAchievements.Add(Achievement.BazaarFlipProfit);
        await Persistence().ForceSave(state);
        await AssertConsumer(Player, true);
    }

    [TestCase(false), TestCase(true)]
    public async Task PurchaseOrLossDoesNotUnlockProfit(bool sellAtLoss)
    {
        var state = await Persistence().GetStateObject(Name);
        await Process(state, Buy);
        if (sellAtLoss)
            await Process(state, "[Bazaar] Claimed 100 coins from selling 64x Coal at 1.6 each!");
        var flips = await Tracker().GetFlips(Player);
        Assert.That(flips.Count, Is.EqualTo(sellAtLoss ? 1 : 0));
        if (sellAtLoss)
            Assert.That(flips[0].Profit, Is.EqualTo(-344));
        await redis.GetDatabase().KeyDeleteAsync("pstate:" + Name);
        await AssertConsumer(Player, false);
    }

    private async Task AssertConsumer(Guid player, bool expected)
    {
        // A fresh HTTP host and persistence service: no consumer cache or live state instance.
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IPersistenceService>(Persistence());
        builder.Services.AddSingleton(names.Object);
        builder.Services.AddControllers().AddApplicationPart(typeof(PlayerStateController).Assembly)
            .AddJsonOptions(o => o.JsonSerializerOptions.IncludeFields = true).AddControllersAsServices();
        builder.Services.AddTransient(_ => new PlayerStateController(_.GetRequiredService<IPersistenceService>(), null!));
        await using var app = builder.Build();
        app.MapControllers();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var json = await http.GetStringAsync($"/PlayerState/{player:N}/achievements");
        var unlocked = JsonConvert.DeserializeObject<HashSet<string>>(json)!;
        Assert.That(unlocked.Contains("BazaarFlipProfit"), Is.EqualTo(expected),
            "Uncached EmblemService UUID HTTP lookup must satisfy menu/equip Contains(\"BazaarFlipProfit\"); response: " + json);
    }
}
