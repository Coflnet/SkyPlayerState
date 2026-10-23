using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Moq;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarOrderSyncTests
{
    private class Backend(bool legacy) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests = new();
        public HttpStatusCode? Failure;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri.AbsolutePath, await request.Content.ReadAsStringAsync(cancellationToken)));
            if (Failure.HasValue)
                return new HttpResponseMessage(Failure.Value);
            return new HttpResponseMessage(legacy && request.RequestUri.AbsolutePath == "/OrderBook/player"
                ? HttpStatusCode.NotFound : HttpStatusCode.NoContent);
        }
    }

    [SetUp]
    public void ResetSyncCache() => BazaarOrderSync.ResetSyncCache();

    private static MockedUpdateArgs ViewArgs(DateTime now, params Offer[] open)
    {
        var args = new MockedUpdateArgs { currentState = new(), msg = new() { UserId = "1", ReceivedAt = now } };
        args.currentState.McInfo.Name = "Ekwav";
        args.currentState.BazaarOffers = open.ToList();
        args.AddService<ILogger<BazaarOrderSync>>(NullLogger<BazaarOrderSync>.Instance);
        return args;
    }

    private static Offer Open(string tag, DateTime created, int filled = 0) => new() {
        ItemName = tag, ItemTag = tag, Amount = 64, FilledAmount = filled, IsSell = false, Created = created, PricePerUnit = 5 };

    [Test]
    public async Task IdenticalConsecutiveViewsSkipThePost()
    {
        using var backend = new Backend(false);
        using var client = new HttpClient(backend) { BaseAddress = new Uri("http://bazaar/") };
        var now = DateTime.UtcNow;
        var args = ViewArgs(now, Open("COAL", now.AddHours(-1)));
        var sync = new BazaarOrderSync(client);
        var skipped = BazaarOrderSync.OrderSyncs.WithLabels("skipped").Value;
        await sync.Observe(args);
        args.msg.ReceivedAt = now.AddSeconds(30);
        await sync.Observe(args);
        args.msg.ReceivedAt = now.AddSeconds(60);
        await sync.Observe(args);
        Assert.That(backend.Requests, Has.Count.EqualTo(1));
        Assert.That(BazaarOrderSync.OrderSyncs.WithLabels("skipped").Value, Is.EqualTo(skipped + 2));
    }

    [Test]
    public async Task ChangedOpenOrdersAreSent()
    {
        using var backend = new Backend(false);
        using var client = new HttpClient(backend) { BaseAddress = new Uri("http://bazaar/") };
        var now = DateTime.UtcNow;
        var coal = Open("COAL", now.AddHours(-1));
        var args = ViewArgs(now, coal);
        var sync = new BazaarOrderSync(client);
        await sync.Observe(args);
        args.msg.ReceivedAt = now.AddSeconds(10);
        args.currentState.BazaarOffers = new() { Open("COAL", coal.Created, filled: 10) };
        await sync.Observe(args); // filled
        args.msg.ReceivedAt = now.AddSeconds(20);
        args.currentState.BazaarOffers.Add(Open("WHEAT", now.AddSeconds(-5)));
        await sync.Observe(args); // new
        args.msg.ReceivedAt = now.AddSeconds(30);
        args.currentState.BazaarOffers = new() { Open("WHEAT", args.currentState.BazaarOffers[1].Created) };
        await sync.Observe(args); // coal vanished
        Assert.That(backend.Requests, Has.Count.EqualTo(4));
    }

    [Test]
    public async Task ClaimedAndNewlyClosedOrdersAreSentButOnlyAsDelta()
    {
        using var backend = new Backend(false);
        using var client = new HttpClient(backend) { BaseAddress = new Uri("http://bazaar/") };
        var now = DateTime.UtcNow;
        var oldClosed = new Offer { ItemName = "Coal", ItemTag = "COAL", Amount = 64, FilledAmount = 64, ClaimedAmount = 64, Created = now.AddDays(-2), PricePerUnit = 2 };
        var args = ViewArgs(now, Open("WHEAT", now.AddHours(-2)));
        BazaarListener.RetainClosedOrder(args.currentState, oldClosed, now.AddDays(-1));
        var sync = new BazaarOrderSync(client);
        await sync.Observe(args);
        Assert.That(JObject.Parse(backend.Requests[0].Body)["orders"], Has.Count.EqualTo(2));

        // an open order closes: it is sent once, the old closed order is not repeated
        args.msg.ReceivedAt = now.AddMinutes(1);
        var wheat = args.currentState.BazaarOffers[0];
        args.currentState.BazaarOffers = new() { Open("IRON", now.AddMinutes(-1)) };
        BazaarListener.RetainClosedOrder(args.currentState, wheat, args.msg.ReceivedAt);
        await sync.Observe(args);
        var delta = JObject.Parse(backend.Requests[1].Body)["orders"];
        Assert.That(delta.Select(o => (string)o["itemId"]), Is.EquivalentTo(new[] { "IRON", "WHEAT" }));

        // unchanged afterwards
        args.msg.ReceivedAt = now.AddMinutes(2);
        await sync.Observe(args);
        Assert.That(backend.Requests, Has.Count.EqualTo(2));

        // a late claim on a closed order is sent
        wheat.ClaimedAmount = 10;
        args.msg.ReceivedAt = now.AddMinutes(3);
        await sync.Observe(args);
        Assert.That(JObject.Parse(backend.Requests[2].Body)["orders"].Select(o => (string)o["itemId"]), Is.EquivalentTo(new[] { "IRON", "WHEAT" }));
    }

    [Test]
    public async Task UnchangedViewIsResentInFullAfterTheInterval()
    {
        using var backend = new Backend(false);
        using var client = new HttpClient(backend) { BaseAddress = new Uri("http://bazaar/") };
        var now = DateTime.UtcNow;
        var args = ViewArgs(now, Open("COAL", now.AddHours(-1)));
        var closed = new Offer { ItemName = "Wheat", ItemTag = "WHEAT", Amount = 64, FilledAmount = 64, ClaimedAmount = 64, Created = now.AddDays(-2) };
        BazaarListener.RetainClosedOrder(args.currentState, closed, now.AddDays(-1));
        var sync = new BazaarOrderSync(client);
        await sync.Observe(args);
        args.msg.ReceivedAt = now.Add(BazaarOrderSync.FullResendInterval).AddSeconds(-1);
        await sync.Observe(args);
        Assert.That(backend.Requests, Has.Count.EqualTo(1));
        args.msg.ReceivedAt = now.Add(BazaarOrderSync.FullResendInterval);
        await sync.Observe(args);
        Assert.That(backend.Requests, Has.Count.EqualTo(2));
        Assert.That(JObject.Parse(backend.Requests[1].Body)["orders"], Has.Count.EqualTo(2));
    }

    [Test]
    public async Task FailedPostDoesNotRecordTheFingerprintSoTheNextViewRetries()
    {
        using var backend = new Backend(false) { Failure = HttpStatusCode.BadRequest };
        using var client = new HttpClient(backend) { BaseAddress = new Uri("http://bazaar/") };
        var now = DateTime.UtcNow;
        var args = ViewArgs(now, Open("COAL", now.AddHours(-1)));
        var sync = new BazaarOrderSync(client);
        var failed = BazaarOrderSync.OrderSyncs.WithLabels("failed").Value;
        Assert.ThrowsAsync<HttpRequestException>(() => sync.Observe(args));
        Assert.That(BazaarOrderSync.OrderSyncs.WithLabels("failed").Value, Is.EqualTo(failed + 1));
        backend.Failure = null;
        args.msg.ReceivedAt = now.AddSeconds(5);
        await sync.Observe(args);
        args.msg.ReceivedAt = now.AddSeconds(10);
        await sync.Observe(args);
        Assert.That(backend.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task TransientFailureRetriesAfterTenSecondsWithoutReplayingStateParsing()
    {
        using var listener = new ActivityListener {
            ShouldListenTo = source => source.Name == BazaarOrderSync.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        var traceIds = new List<ActivityTraceId>();
        var retries = BazaarOrderSync.Syncs.WithLabels("observe", "retry").Value;
        var attempts = 0;
        var elapsed = Stopwatch.StartNew();
        await BazaarOrderSync.Retry(() => {
            traceIds.Add(Activity.Current.TraceId);
            if (++attempts == 1)
                throw new HttpRequestException("SkyBazaar loading", null, HttpStatusCode.ServiceUnavailable);
            return Task.CompletedTask;
        }, NullLogger.Instance);
        Assert.That(traceIds[0], Is.EqualTo(traceIds[1]));
        Assert.That(BazaarOrderSync.Syncs.WithLabels("observe", "retry").Value, Is.EqualTo(retries + 1));
        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(elapsed.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(10)));
    }

    [Test]
    public void InvalidObservationIsNotRetried()
    {
        var attempts = 0;
        Assert.ThrowsAsync<HttpRequestException>(() => BazaarOrderSync.Retry(() => {
            attempts++;
            throw new HttpRequestException("bad input", null, HttpStatusCode.BadRequest);
        }, NullLogger.Instance));
        Assert.That(attempts, Is.EqualTo(1));
    }

    [Test]
    public async Task ChatOnlyPartialClaimResolvesTagsWithoutNeedingAMenu()
    {
        using var backend = new Backend(false);
        using var client = new HttpClient(backend) { BaseAddress = new Uri("http://bazaar/") };
        var args = new MockedUpdateArgs { currentState = new(), msg = new() { UserId = "1", ReceivedAt = DateTime.UtcNow } };
        args.currentState.McInfo.Name = "Ekwav";
        args.currentState.BazaarOffers.Add(new() { ItemName = "Claim Tag Test", Amount = 1024,
            FilledAmount = 512, ClaimedAmount = 512, Created = DateTime.UtcNow });
        var items = new Mock<Coflnet.Sky.Items.Client.Api.IItemsApi>();
        items.Setup(i => i.ItemsSearchTermGetAsync("Claim Tag Test", null, 0, default))
            .ReturnsAsync(new List<Coflnet.Sky.Items.Client.Model.SearchResult> { new() { Tag = "CLAIM_TAG_TEST" } });
        args.AddService(items.Object);
        args.AddService<ILogger<BazaarOrderSync>>(NullLogger<BazaarOrderSync>.Instance);
        // Another local order has stale fills and must not be submitted by this claim.
        args.currentState.BazaarOffers.Add(new() { ItemTag = "OTHER", Amount = 64 });
        await new BazaarOrderSync(client).Claim(args, args.currentState.BazaarOffers[0]);
        Assert.That(backend.Requests.Single().Path, Is.EqualTo("/OrderBook"));
        Assert.That((string)JObject.Parse(backend.Requests.Single().Body)["itemId"], Is.EqualTo("CLAIM_TAG_TEST"));
        Assert.That((int)JObject.Parse(backend.Requests.Single().Body)["claimed"], Is.EqualTo(512));
    }

    [Test]
    public async Task SnapshotCarriesExpiryAndAlreadyClaimedQuantity()
    {
        using var backend = new Backend(false);
        using var client = new HttpClient(backend) { BaseAddress = new Uri("http://bazaar/") };
        var args = new MockedUpdateArgs { currentState = new(), msg = new() { UserId = "1", ReceivedAt = DateTime.UtcNow } };
        args.currentState.McInfo.Name = "Ekwav";
        args.currentState.BazaarOffers.Add(new() { ItemTag = "GILL_MEMBRANE", Amount = 1024,
            FilledAmount = 1024, ClaimedAmount = 512, IsExpired = true, Created = DateTime.UtcNow });
        args.AddService<ILogger<BazaarOrderSync>>(NullLogger<BazaarOrderSync>.Instance);
        await new BazaarOrderSync(client).Observe(args);
        var order = JObject.Parse(backend.Requests[0].Body)["orders"][0];
        Assert.That((bool)order["isExpired"], Is.True);
        Assert.That((int)order["claimed"], Is.EqualTo(512));
        Assert.That((int)order["filled"], Is.EqualTo(1024));
    }

    [Test]
    public async Task ClosedOrdersRemainInPersonalSnapshotsWithTheirExactIdentityAndCumulativeClaims()
    {
        using var backend = new Backend(false);
        using var client = new HttpClient(backend) { BaseAddress = new Uri("http://bazaar/") };
        var now = DateTime.UtcNow;
        var args = new MockedUpdateArgs { currentState = new(), msg = new() { UserId = "1", ReceivedAt = now } };
        args.currentState.McInfo.Name = "Ekwav";
        args.AddService<ILogger<BazaarOrderSync>>(NullLogger<BazaarOrderSync>.Instance);
        var closed = new Offer { ItemName = "Coal", ItemTag = "COAL", Amount = 64,
            FilledAmount = 64, ClaimedAmount = 64, Created = now.AddDays(-9), IsSell = true };
        BazaarListener.RetainClosedOrder(args.currentState, closed, now);
        await new BazaarOrderSync(client).Observe(args);
        var sent = JObject.Parse(backend.Requests.Single().Body)["orders"][0];
        Assert.That((int)sent["claimed"], Is.EqualTo(64));
        Assert.That((bool)sent["isExpired"], Is.True);
        Assert.That((DateTime)sent["timestamp"], Is.EqualTo(closed.Created));
        args.msg.ReceivedAt = now.AddDays(8);
        await new BazaarOrderSync(client).Observe(args);
        Assert.That(JObject.Parse(backend.Requests.Last().Body)["orders"], Is.Empty);
    }

    [Test]
    public void ClosedOrderRetentionKeepsOnlyTheMostRecentOrders()
    {
        var state = new Models.StateObject();
        var now = DateTime.UtcNow;
        for (var i = 0; i < BazaarListener.MaxClosedOrders + 20; i++)
            BazaarListener.RetainClosedOrder(state, new Offer { ItemName = "Coal", Created = now.AddMinutes(-i - 1) }, now.AddSeconds(i));
        Assert.That(state.ClosedBazaarOffers.Count, Is.EqualTo(BazaarListener.MaxClosedOrders));
        Assert.That(state.ClosedBazaarOffers.Min(o => o.ClosedAt), Is.EqualTo(now.AddSeconds(20)));
    }

    [Test]
    public async Task NewPlayerStateCanStillRegisterOrdersOnAnOlderBazaar()
    {
        using var backend = new Backend(legacy: true);
        using var client = new HttpClient(backend) { BaseAddress = new Uri("http://bazaar/") };
        var args = new MockedUpdateArgs { currentState = new(), msg = new() { UserId = "1", ReceivedAt = DateTime.UtcNow } };
        args.currentState.McInfo.Name = "Ekwav";
        args.currentState.BazaarOffers = new() { new() {
            ItemTag = "WHEAT", Amount = 64, FilledAmount = 16, IsSell = true, Created = DateTime.UtcNow.AddMinutes(-1)
        } };
        args.AddService<ILogger<BazaarOrderSync>>(NullLogger<BazaarOrderSync>.Instance);
        await new BazaarOrderSync(client).Observe(args);
        Assert.That(backend.Requests.ConvertAll(r => r.Path), Is.EqualTo(new[] { "/OrderBook/player", "/OrderBook" }));
        var legacy = JObject.Parse(backend.Requests[1].Body);
        Assert.That((string)legacy["userId"], Is.EqualTo("1"));
        Assert.That((string)legacy["playerName"], Is.EqualTo("Ekwav"));
        Assert.That((int)legacy["filled"], Is.EqualTo(16));
    }
}
