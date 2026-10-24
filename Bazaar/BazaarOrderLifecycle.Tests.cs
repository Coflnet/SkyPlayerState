using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.EventBroker.Client.Api;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarOrderLifecycleTests
{
    private class FakeProducer : IBazaarOrderLifecycleProducer
    {
        public List<BazaarOrderLifecycleEvent> Events = new();
        public void Emit(UpdateArgs args, Offer offer, string source, string state, string itemTagOverride = null)
        {
            var e = BazaarOrderLifecycleEvent.TryCreate(args, offer, source, state, itemTagOverride);
            if (e != null)
                Events.Add(e);
        }
    }

    private class Backend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
    }

    private static readonly Guid Player = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private FakeProducer producer = null!;
    private StateObject state = null!;
    private BazaarOrderListener listener = new();

    [SetUp]
    public void Setup()
    {
        BazaarOrderSync.ResetSyncCache();
        producer = new FakeProducer();
        state = new();
        state.McInfo.Uuid = Player;
        state.McInfo.Name = "Ekwav";
    }

    private MockedUpdateArgs Args(string userId, DateTime at, bool withProducer = true, params string[] chat)
    {
        var args = new MockedUpdateArgs { currentState = state, msg = new() { ChatBatch = chat.ToList(), UserId = userId, ReceivedAt = at } };
        var items = new Mock<IItemsApi>();
        items.Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync(new List<Items.Client.Model.SearchResult> { new() { Tag = "COAL", Text = "Coal" } });
        args.AddService(items.Object);
        args.AddService(new Mock<ITransactionService>().Object);
        args.AddService(new Mock<IOrderBookApi>().Object);
        args.AddService(new Mock<IScheduleApi>().Object);
        args.AddService(new Mock<BazaarOrderSync>(new HttpClient()).Object);
        args.AddService<ILogger<BazaarOrderListener>>(NullLogger<BazaarOrderListener>.Instance);
        args.AddService<ILogger<BazaarOrderSync>>(NullLogger<BazaarOrderSync>.Instance);
        if (withProducer)
            args.AddService<IBazaarOrderLifecycleProducer>(producer);
        return args;
    }

    private static Offer Coal(DateTime created, bool sell = false, long filled = 0, long amount = 64) => new() {
        ItemName = "Coal", ItemTag = "COAL", Amount = amount, FilledAmount = filled, IsSell = sell, Created = created, PricePerUnit = 5 };

    [Test]
    public async Task Setup_EmitsChatSetupOpenWithReceivedAtCreated()
    {
        var at = DateTime.UtcNow;
        await listener.Process(Args("5", at, true, "[Bazaar] Buy Order Setup! 64x Coal for 134.4 coins"));
        var e = producer.Events.Should().ContainSingle().Subject;
        e.Source.Should().Be("chat-setup");
        e.State.Should().Be("Open");
        e.ItemTag.Should().Be("COAL");
        e.Created.Should().Be(at);
        e.PlayerUuid.Should().Be(Player);
        e.Amount.Should().Be(64);
        e.PricePerUnit.Should().Be(134.4 / 64);
        e.SchemaVersion.Should().Be(1);
    }

    [Test]
    public async Task Filled_EmitsChatFilled()
    {
        state.BazaarOffers.Add(Coal(DateTime.UtcNow.AddMinutes(-1)));
        await listener.Process(Args("5", DateTime.UtcNow, true, "[Bazaar] Your Buy Order for 64x Coal was filled!"));
        var e = producer.Events.Should().ContainSingle().Subject;
        e.Source.Should().Be("chat-filled");
        e.State.Should().Be("Filled");
        e.FilledAmount.Should().Be(64);
    }

    [Test]
    public async Task Cancelled_EmitsCancelledWithClosedAt()
    {
        var at = DateTime.UtcNow;
        state.BazaarOffers.Add(Coal(at.AddMinutes(-1)));
        await listener.Process(Args("5", at, true, "[Bazaar] Cancelled! Refunded 320 coins from cancelling Buy Order!"));
        var e = producer.Events.Should().ContainSingle().Subject;
        e.Source.Should().Be("chat-cancelled");
        e.State.Should().Be("Cancelled");
        e.ClosedAt.Should().Be(at);
    }

    [Test]
    public async Task Claim_EmitsClaimedThenClosedWhenFullyClaimed()
    {
        var at = DateTime.UtcNow;
        state.BazaarOffers.Add(Coal(at.AddMinutes(-1), amount: 1024, filled: 1024));
        state.BazaarOffers[0].PricePerUnit = 7.1;
        var msg = "[Bazaar] Claimed 512x Coal worth 3,635.2 coins bought for 7.1 each!";
        await listener.Process(Args("5", at, true, msg));
        producer.Events.Select(e => (e.Source, e.State)).Should().Equal(("chat-claim", "Claimed"));
        producer.Events[0].ClaimedAmount.Should().Be(512);
        await listener.Process(Args("5", at.AddSeconds(1), true, msg));
        producer.Events.Select(e => e.State).Should().Equal("Claimed", "Closed");
        producer.Events[1].ClosedAt.Should().NotBeNull();
        producer.Events[1].ClaimedAmount.Should().Be(1024);
    }

    private static (BazaarOrderSync sync, HttpClient client) NewSync()
    {
        var client = new HttpClient(new Backend()) { BaseAddress = new Uri("http://bazaar/") };
        return (new BazaarOrderSync(client), client);
    }

    [Test]
    public async Task Observe_EmitsOnlyChangedOrdersUnlessFullResend()
    {
        var (sync, client) = NewSync();
        using var _ = client;
        var now = DateTime.UtcNow;
        var a = Coal(now.AddHours(-2));
        var b = Coal(now.AddHours(-1), sell: true);
        state.BazaarOffers = new() { a, b };
        var args = Args("1", now);
        await sync.Observe(args);
        producer.Events.Should().HaveCount(2).And.OnlyContain(e => e.Source == "gui-view" && e.State == "Open");

        producer.Events.Clear();
        args.msg.ReceivedAt = now.AddSeconds(30);
        state.BazaarOffers = new() { a, Coal(b.Created, sell: true, filled: 10) };
        await sync.Observe(args);
        var changed = producer.Events.Should().ContainSingle().Subject;
        changed.IsSell.Should().BeTrue();
        changed.FilledAmount.Should().Be(10);

        producer.Events.Clear();
        args.msg.ReceivedAt = now.Add(BazaarOrderSync.FullResendInterval).AddMinutes(1);
        await sync.Observe(args);
        producer.Events.Should().HaveCount(2).And.OnlyContain(e => e.Source == "gui-full-resend");
    }

    [Test]
    public async Task Observe_UnresolvedTagNeverEmitted()
    {
        var (sync, client) = NewSync();
        using var _ = client;
        var now = DateTime.UtcNow;
        state.BazaarOffers = new() { new() { ItemName = "Unknown Thing", Amount = 1, Created = now, PricePerUnit = 1 } };
        var args = Args("1", now);
        Mock.Get(args.GetService<IItemsApi>()).Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync(new List<Items.Client.Model.SearchResult>());
        await sync.Observe(args);
        producer.Events.Should().BeEmpty();
    }

    [Test]
    public async Task LoggedOut_NoEmit()
    {
        await listener.Process(Args(null, DateTime.UtcNow, true, "[Bazaar] Buy Order Setup! 64x Coal for 134.4 coins"));
        producer.Events.Should().BeEmpty();
        var args = Args(null, DateTime.UtcNow);
        BazaarOrderLifecycleEvent.TryCreate(args, Coal(DateTime.UtcNow), "gui-view", "Open").Should().BeNull();
        args = Args("1", DateTime.UtcNow);
        state.McInfo.Uuid = Guid.Empty;
        BazaarOrderLifecycleEvent.TryCreate(args, Coal(DateTime.UtcNow), "gui-view", "Open").Should().BeNull();
        state.McInfo.Uuid = Player;
        BazaarOrderLifecycleEvent.TryCreate(args, new Offer { ItemName = "x" }, "gui-view", "Open").Should().BeNull();
    }

    [Test]
    public void EventId_StableForSameInputs_DiffersPerObservedAt()
    {
        var at = DateTime.UtcNow;
        var offer = Coal(at.AddHours(-1));
        var first = BazaarOrderLifecycleEvent.TryCreate(Args("1", at), offer, "gui-view", "Open")!;
        var again = BazaarOrderLifecycleEvent.TryCreate(Args("1", at), offer, "gui-view", "Open")!;
        var later = BazaarOrderLifecycleEvent.TryCreate(Args("1", at.AddSeconds(1)), offer, "gui-view", "Open")!;
        again.EventId.Should().Be(first.EventId).And.MatchRegex("^[0-9a-f]{64}$");
        later.EventId.Should().NotBe(first.EventId);
        later.Revision.Should().Be(first.Revision);
    }

    [Test]
    public async Task ExistingTestsStillPassWithoutProducerRegistered()
    {
        // Same flows with no producer registered must keep working and not throw.
        var at = DateTime.UtcNow;
        await listener.Process(Args("5", at, false, "[Bazaar] Buy Order Setup! 64x Coal for 134.4 coins"));
        state.BazaarOffers.Should().ContainSingle();
        var (sync, client) = NewSync();
        using var _ = client;
        await sync.Observe(Args("1", at.AddSeconds(5), false));
    }
}
