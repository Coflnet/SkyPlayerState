using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.EventBroker.Client.Api;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.PlayerState.Controllers;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Bazaar.History;

public class BazaarHistoryTests
{
    private const string Salt = "test-salt";
    private static readonly Guid Player = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTime T0 = new(2026, 10, 20, 23, 59, 0, DateTimeKind.Utc);

    private class MemoryStore : IBazaarHistoryStore
    {
        public ConcurrentDictionary<string, BazaarOrderHistoryRow> Orders = new();
        public ConcurrentBag<BazaarFlipHistoryRow> Flips = new();
        public bool Fail;
        public int Upserts;
        public List<(int Partition, int PageSize)> Queries = new();
        public Func<string, int, HistoryPage<BazaarOrderHistoryRow>>? OrderPages;

        public static string Key(string tag, DateTime created, string hash, bool sell) => $"{tag}|{created.Ticks}|{hash}|{sell}";
        public Task<BazaarOrderHistoryRow?> GetOrder(string itemTag, DateTime day, DateTime created, string accountHash, bool isSell)
        {
            if (Fail) throw new InvalidOperationException("scylla down");
            Reads++;
            Orders.TryGetValue(Key(itemTag, created, accountHash, isSell), out var row);
            return Task.FromResult(row?.Clone());
        }
        public List<(OrderColumns Columns, Dictionary<DateTime, long> Points)> WriteLog = new();
        public int Reads;
        public Task WriteOrder(BazaarOrderHistoryRow row, OrderColumns columns, IReadOnlyDictionary<DateTime, long> newPoints, int ttlSeconds)
        {
            if (Fail) throw new InvalidOperationException("scylla down");
            Upserts++;
            WriteLog.Add((columns, new Dictionary<DateTime, long>(newPoints)));
            // emulate CQL UPDATE: only the listed columns and appended fill points change
            var key = Key(row.ItemTag, row.Created, row.AccountHash, row.IsSell);
            var stored = Orders.GetOrAdd(key, _ => new BazaarOrderHistoryRow { ItemTag = row.ItemTag, Day = row.Day, Created = row.Created, AccountHash = row.AccountHash, IsSell = row.IsSell, State = "", FirstSeenSource = "" });
            if (columns.HasFlag(OrderColumns.Price)) stored.PricePerUnit = row.PricePerUnit;
            if (columns.HasFlag(OrderColumns.Amount)) stored.Amount = row.Amount;
            if (columns.HasFlag(OrderColumns.Filled)) stored.FilledAmount = row.FilledAmount;
            if (columns.HasFlag(OrderColumns.Claimed)) stored.ClaimedAmount = row.ClaimedAmount;
            if (columns.HasFlag(OrderColumns.Expired)) stored.IsExpired = row.IsExpired;
            if (columns.HasFlag(OrderColumns.ClosedAt)) stored.ClosedAt = row.ClosedAt;
            if (columns.HasFlag(OrderColumns.State)) stored.State = row.State;
            if (columns.HasFlag(OrderColumns.LastObserved)) stored.LastObservedAt = row.LastObservedAt;
            if (columns.HasFlag(OrderColumns.FirstSeen)) stored.FirstSeenSource = row.FirstSeenSource;
            if (columns.HasFlag(OrderColumns.FullFill)) stored.FullFillAt = row.FullFillAt;
            foreach (var p in newPoints) stored.FillTimes[p.Key] = p.Value;
            return Task.CompletedTask;
        }
        public Task UpsertFlip(BazaarFlipHistoryRow row, int ttlSeconds)
        {
            if (Fail) throw new InvalidOperationException("scylla down");
            Flips.Add(row);
            return Task.CompletedTask;
        }
        public Task<HistoryPage<BazaarOrderHistoryRow>> QueryOrders(string itemTag, DateTime day, DateTime from, DateTime to, int pageSize, byte[]? pagingState)
        {
            Queries.Add(((int)(day - from.Date).TotalDays, pageSize));
            return Task.FromResult(OrderPages?.Invoke(day.ToString("yyyyMMdd") + (pagingState == null ? "" : "+"), pageSize) ?? new HistoryPage<BazaarOrderHistoryRow>());
        }
        public Task<HistoryPage<BazaarFlipHistoryRow>> QueryFlips(string itemTag, int month, DateTime from, DateTime to, int pageSize, byte[]? pagingState)
        {
            Queries.Add((month, pageSize));
            return Task.FromResult(new HistoryPage<BazaarFlipHistoryRow>());
        }
    }

    private static BazaarHistoryOptions Options(string? salt = Salt, int readConcurrency = 4) =>
        new() { Salt = salt, ReadConcurrency = readConcurrency, WriteConcurrency = 2, QueueCapacity = 100 };

    private static BazaarHistoryWriter Writer(MemoryStore store, BazaarHistoryOptions? o = null) =>
        new(store, o ?? Options(), NullLogger<BazaarHistoryWriter>.Instance);

    private static BazaarOrderObservation Obs(long filled, DateTime observedAt, long amount = 64, string source = "gui-view", params (DateTime, long)[] fills) => new()
    {
        ItemTag = "COAL", AccountHash = "h", IsSell = false, Created = T0, PricePerUnit = 5, Amount = amount, FilledAmount = filled,
        State = filled >= amount ? "Filled" : "Open", ObservedAt = observedAt, Source = source, Fills = fills.ToList()
    };

    private static async Task Drain(BazaarHistoryWriter w)
    {
        for (var i = 0; i < 200 && w.Pending > 0; i++)
            await Task.Delay(10);
        w.Pending.Should().Be(0);
    }

    // ---- keys and hashing ----

    [Test]
    public void DayAndMonthPartitionsAreUtc()
    {
        BazaarHistoryKeys.DayOf(new DateTime(2026, 10, 20, 23, 59, 59, DateTimeKind.Utc)).Should().Be(new DateTime(2026, 10, 20));
        // an offset time late in the evening belongs to the next UTC day
        BazaarHistoryKeys.DayOf(new DateTime(2026, 10, 20, 23, 30, 0, DateTimeKind.Utc).AddHours(1)).Should().Be(new DateTime(2026, 10, 21));
        // unspecified kind is treated as UTC rather than local
        BazaarHistoryKeys.DayOf(new DateTime(2026, 10, 20, 1, 0, 0, DateTimeKind.Unspecified)).Should().Be(new DateTime(2026, 10, 20));
        BazaarHistoryKeys.MonthOf(new DateTime(2026, 1, 31, 23, 0, 0, DateTimeKind.Utc)).Should().Be(202601);
        BazaarHistoryKeys.MonthStart(202612).Should().Be(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void HashIsDeterministicSaltedAndNotTheUuid()
    {
        var a = BazaarAccountHasher.Hash("salt-a", Player);
        a.Should().Be(BazaarAccountHasher.Hash("salt-a", Player));
        a.Should().NotBe(BazaarAccountHasher.Hash("salt-b", Player));
        a.Should().NotContain(Player.ToString("N"));
        a.Should().NotContain(Player.ToString());
        a.Should().HaveLength(64);
    }

    [Test]
    public async Task RowsNeverContainRawUuidOrName()
    {
        var store = new MemoryStore();
        using var writer = Writer(store);
        var state = new StateObject();
        state.McInfo.Uuid = Player;
        state.McInfo.Name = "Ekwav";
        var args = new MockedUpdateArgs { currentState = state, msg = new() { UserId = "u", ReceivedAt = T0 } };
        var offer = new Offer { ItemTag = "COAL", ItemName = "Coal", Amount = 64, Created = T0, PricePerUnit = 5, FilledAmount = 10,
            Customers = new() { new Fill { PlayerName = "Ekwav", Amount = 10, TimeStamp = T0.AddMinutes(1) } } };
        var e = BazaarOrderLifecycleEvent.TryCreate(args, offer, "chat-setup", "Open")!;
        writer.EnqueueOrder(e, offer.Customers);
        writer.EnqueueFlip(new CompletedBazaarFlip { PlayerUuid = Player, ItemTag = "COAL", Amount = 1, SoldAt = T0 });
        await Drain(writer);

        var json = JsonSerializer.Serialize(store.Orders.Values) + JsonSerializer.Serialize(store.Flips);
        json.Should().NotContain(Player.ToString()).And.NotContain(Player.ToString("N")).And.NotContain("Ekwav");
        store.Orders.Values.Single().AccountHash.Should().Be(BazaarAccountHasher.Hash(Salt, Player));
        store.Flips.Single().AccountHash.Should().Be(BazaarAccountHasher.Hash(Salt, Player));
    }

    [Test]
    public void WithoutSaltFeatureIsDisabledAndWritesNothing()
    {
        var store = new MemoryStore();
        using var writer = Writer(store, Options(salt: null));
        writer.Enabled.Should().BeFalse();
        writer.EnqueueFlip(new CompletedBazaarFlip { PlayerUuid = Player, ItemTag = "COAL", SoldAt = T0 });
        writer.Pending.Should().Be(0);
        store.Flips.Should().BeEmpty();
    }

    [Test]
    public void OptionsReadConfigWithDefaults()
    {
        var empty = BazaarHistoryOptions.From(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        empty.Enabled.Should().BeFalse();
        empty.TtlDays.Should().Be(180);
        empty.ReadConcurrency.Should().Be(4);
        var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["BAZAAR_HISTORY:SALT"] = "x", ["BAZAAR_HISTORY:TTL_DAYS"] = "30", ["BAZAAR_HISTORY:BACKFILL"] = "true" }).Build();
        var o = BazaarHistoryOptions.From(cfg);
        o.Enabled.Should().BeTrue();
        o.TtlSeconds.Should().Be(30 * 86400);
        o.Backfill.Should().BeTrue();
    }

    // ---- merge semantics ----

    [Test]
    public void FillTimesFromCustomersAreCumulativeAndExact()
    {
        var row = BazaarOrderHistoryMerge.Apply(null, Obs(30, T0.AddMinutes(10), 64, "gui-view",
            (T0.AddMinutes(2), 10), (T0.AddMinutes(1), 20)));
        row.FillTimes.Should().BeEquivalentTo(new Dictionary<DateTime, long> { [T0.AddMinutes(1)] = 20, [T0.AddMinutes(2)] = 30 });
        row.FullFillAt.Should().BeNull();
    }

    [Test]
    public void FilledAmountIncreaseWithoutCustomersAddsObservationPointOnce()
    {
        var row = BazaarOrderHistoryMerge.Apply(null, Obs(10, T0.AddMinutes(1)));
        row = BazaarOrderHistoryMerge.Apply(row, Obs(10, T0.AddMinutes(2))); // unchanged: no new point
        row = BazaarOrderHistoryMerge.Apply(row, Obs(25, T0.AddMinutes(3)));
        row.FillTimes.Should().BeEquivalentTo(new Dictionary<DateTime, long> { [T0.AddMinutes(1)] = 10, [T0.AddMinutes(3)] = 25 });
        row.LastObservedAt.Should().Be(T0.AddMinutes(3));
    }

    [Test]
    public void ReplayingTheSameObservationIsIdempotent()
    {
        var o = Obs(10, T0.AddMinutes(1), 64, "gui-view", (T0.AddSeconds(30), 10));
        var once = BazaarOrderHistoryMerge.Apply(null, o);
        var twice = BazaarOrderHistoryMerge.Apply(once, o);
        JsonSerializer.Serialize(twice).Should().Be(JsonSerializer.Serialize(once));
    }

    [Test]
    public void FullFillAtIsSetOnceAndNeverMoves()
    {
        var row = BazaarOrderHistoryMerge.Apply(null, Obs(64, T0.AddMinutes(5)));
        row.FullFillAt.Should().Be(T0.AddMinutes(5));
        row = BazaarOrderHistoryMerge.Apply(row, Obs(64, T0.AddMinutes(9), 64, "chat-claim"));
        row.FullFillAt.Should().Be(T0.AddMinutes(5));
    }

    [Test]
    public void FullFillAtUsesCustomerTimestampWhenAvailable()
    {
        var row = BazaarOrderHistoryMerge.Apply(null, Obs(64, T0.AddMinutes(30), 64, "gui-view", (T0.AddMinutes(1), 40), (T0.AddMinutes(2), 24)));
        row.FullFillAt.Should().Be(T0.AddMinutes(2));
    }

    [Test]
    public void FirstSeenSourceAndStateOnlyMoveForward()
    {
        var row = BazaarOrderHistoryMerge.Apply(null, Obs(0, T0, 64, "chat-setup"));
        row = BazaarOrderHistoryMerge.Apply(row, new BazaarOrderObservation { ItemTag = "COAL", AccountHash = "h", Created = T0, Amount = 64, FilledAmount = 64, State = "Closed", ClosedAt = T0.AddHours(1), ObservedAt = T0.AddHours(1), Source = "gui-view", ClaimedAmount = 64 });
        row = BazaarOrderHistoryMerge.Apply(row, Obs(64, T0.AddHours(2), 64, "gui-full-resend")); // stale "Filled" must not reopen
        row.FirstSeenSource.Should().Be("chat-setup");
        row.State.Should().Be("Closed");
        row.ClosedAt.Should().Be(T0.AddHours(1));
    }

    [Test]
    public void BackfillWithoutCustomersDoesNotInventFillTimes()
    {
        var o = Obs(32, T0.AddDays(1), 64, BazaarOrderHistoryMerge.SourceBackfill);
        o.IsBackfill = true;
        var row = BazaarOrderHistoryMerge.Apply(null, o);
        row.FillTimes.Should().BeEmpty();
        row.FirstSeenSource.Should().Be("backfill");
    }

    // ---- writer ----

    [Test]
    public async Task WriterAccumulatesAcrossUpsertsUsingExistingRow()
    {
        var store = new MemoryStore();
        using var writer = Writer(store);
        await writer.Process(Obs(10, T0.AddMinutes(1)).WithHash("x"));
        await writer.Process(Obs(64, T0.AddMinutes(4)).WithHash("x"));
        var row = store.Orders.Values.Single();
        row.FillTimes.Count.Should().Be(2);
        row.FullFillAt.Should().Be(T0.AddMinutes(4));
    }

    [Test]
    public async Task WriteFailuresAreSwallowed()
    {
        var store = new MemoryStore { Fail = true };
        using var writer = Writer(store);
        Func<Task> act = () => writer.Process(Obs(1, T0).WithHash("x"));
        await act.Should().NotThrowAsync();
        Func<Task> flip = () => writer.Process(BazaarOrderHistoryMerge.ToFlipRow(new CompletedBazaarFlip { ItemTag = "COAL", SoldAt = T0 }, "x"));
        await flip.Should().NotThrowAsync();
    }

    [Test]
    public async Task FullQueueDropsInsteadOfBlockingOrThrowing()
    {
        var gate = new TaskCompletionSource();
        var store = new BlockingStore(gate.Task);
        using var writer = new BazaarHistoryWriter(store, new BazaarHistoryOptions { Salt = Salt, WriteConcurrency = 1, QueueCapacity = 3 }, NullLogger<BazaarHistoryWriter>.Instance);
        for (var i = 0; i < 50; i++)
            writer.EnqueueFlip(new CompletedBazaarFlip { PlayerUuid = Player, ItemTag = "COAL", SoldAt = T0.AddSeconds(i) });
        writer.Pending.Should().BeLessThan(10);
        gate.SetResult();
        await Drain(writer);
    }

    private class BlockingStore : MemoryStore2
    {
        public BlockingStore(Task gate) : base(gate) { }
    }

    private class MemoryStore2 : IBazaarHistoryStore
    {
        private readonly Task gate;
        public MemoryStore2(Task gate) { this.gate = gate; }
        public Task<BazaarOrderHistoryRow?> GetOrder(string itemTag, DateTime day, DateTime created, string accountHash, bool isSell) => Task.FromResult<BazaarOrderHistoryRow?>(null);
        public Task WriteOrder(BazaarOrderHistoryRow row, OrderColumns columns, IReadOnlyDictionary<DateTime, long> newPoints, int ttlSeconds) => gate;
        public Task UpsertFlip(BazaarFlipHistoryRow row, int ttlSeconds) => gate;
        public virtual Task<HistoryPage<BazaarOrderHistoryRow>> QueryOrders(string itemTag, DateTime day, DateTime from, DateTime to, int pageSize, byte[]? pagingState) => throw new NotSupportedException();
        public Task<HistoryPage<BazaarFlipHistoryRow>> QueryFlips(string itemTag, int month, DateTime from, DateTime to, int pageSize, byte[]? pagingState) => throw new NotSupportedException();
    }

    [Test]
    public async Task BackfillWritesLiveAndClosedOffersOnlyWhenEnabled()
    {
        var state = new StateObject();
        state.McInfo.Uuid = Player;
        state.BazaarOffers.Add(new Offer { ItemTag = "COAL", Amount = 10, Created = T0, PricePerUnit = 2 });
        state.ClosedBazaarOffers = new() { new Offer { ItemTag = "IRON_INGOT", Amount = 5, FilledAmount = 5, Created = T0.AddHours(1), ClosedAt = T0.AddHours(2) } };

        var off = new MemoryStore();
        using (var w = Writer(off))
        {
            w.EnqueueBackfill(state, T0.AddDays(3));
            await Drain(w);
        }
        off.Orders.Should().BeEmpty();

        var on = new MemoryStore();
        using (var w = Writer(on, new BazaarHistoryOptions { Salt = Salt, Backfill = true, WriteConcurrency = 1 }))
        {
            w.EnqueueBackfill(state, T0.AddDays(3));
            await Drain(w);
        }
        on.Orders.Values.Select(r => r.ItemTag).Should().BeEquivalentTo(new[] { "COAL", "IRON_INGOT" });
        on.Orders.Values.Should().OnlyContain(r => r.FirstSeenSource == "backfill");
        on.Orders.Values.Single(r => r.ItemTag == "IRON_INGOT").State.Should().Be("Closed");
    }

    // ---- hook next to the lifecycle emission ----

    [Test]
    public async Task LifecycleEmissionForwardsToHistoryAndSurvivesWriterFailure()
    {
        var state = new StateObject();
        state.McInfo.Uuid = Player;
        var args = new MockedUpdateArgs { currentState = state, msg = new() { UserId = "u", ReceivedAt = T0 } };
        var offer = new Offer { ItemTag = "COAL", ItemName = "Coal", Amount = 64, Created = T0, PricePerUnit = 5 };

        var writer = new Mock<IBazaarOrderHistoryWriter>();
        writer.SetupGet(w => w.Enabled).Returns(true);
        args.AddService(writer.Object);
        await BazaarOrderLifecycle.EmitResolved(args, offer, "chat-setup", "Open");
        writer.Verify(w => w.EnqueueOrder(It.Is<BazaarOrderLifecycleEvent>(e => e.ItemTag == "COAL" && e.Source == "chat-setup"), It.IsAny<IEnumerable<Fill>>()), Times.Once);

        writer.Setup(w => w.EnqueueOrder(It.IsAny<BazaarOrderLifecycleEvent>(), It.IsAny<IEnumerable<Fill>>())).Throws(new Exception("boom"));
        Func<Task> act = () => BazaarOrderLifecycle.EmitResolved(args, offer, "chat-setup", "Open");
        await act.Should().NotThrowAsync();
        BazaarOrderLifecycle.Emit(args, offer, "gui-view", "Open"); // sync variant must not throw either
    }

    [Test]
    public async Task ClaimedSellFlipIsQueuedForPerItemHistory()
    {
        var flip = new CompletedBazaarFlip { PlayerUuid = Player, ItemTag = "COAL", ItemName = "Coal", Amount = 64, BuyPrice = 1344, SellPrice = 3037, Profit = 1693, SoldAt = T0 };
        var tracker = new Mock<IBazaarProfitTracker>();
        tracker.Setup(p => p.RecordSellOrder(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<DateTime>())).ReturnsAsync(flip);
        var writer = new Mock<IBazaarOrderHistoryWriter>();
        var items = new Mock<IItemsApi>();
        items.Setup(i => i.ItemsSearchTermGetAsync(It.IsAny<string>(), null, 0, default))
            .ReturnsAsync(new List<Items.Client.Model.SearchResult> { new() { Tag = "COAL", Flags = new Items.Client.Model.ItemFlags?(Items.Client.Model.ItemFlags.BAZAAR) } });
        var state = new StateObject();
        state.McInfo.Uuid = Player;
        state.McInfo.Name = "Ekwav";
        state.BazaarOffers.Add(new Offer { Amount = 64, ItemName = "Coal", PricePerUnit = 4.8, IsSell = true, Created = DateTime.UtcNow.AddHours(-1) });
        var args = new MockedUpdateArgs { currentState = state, msg = new() { UserId = "12345678-1234-1234-1234-123456789012", ReceivedAt = DateTime.UtcNow,
            ChatBatch = new() { "[Bazaar] Claiming order...", "[Bazaar] Claimed 303.7 coins from selling 64x Coal at 4.8 each!" } } };
        args.AddService(items.Object);
        args.AddService(new Mock<ITransactionService>().Object);
        args.AddService(tracker.Object);
        args.AddService(new Mock<IOrderBookApi>().Object);
        args.AddService(new Mock<BazaarOrderSync>(new System.Net.Http.HttpClient()).Object);
        args.AddService(new Mock<IScheduleApi>().Object);
        args.AddService<ILogger<BazaarOrderListener>>(NullLogger<BazaarOrderListener>.Instance);
        args.AddService(writer.Object);

        await new BazaarOrderListener().Process(args);

        writer.Verify(w => w.EnqueueFlip(flip), Times.Once);
    }

    [Test]
    public async Task FlipRowIsWrittenWithMonthPartitionAndHash()
    {
        var store = new MemoryStore();
        using var writer = Writer(store);
        writer.EnqueueFlip(new CompletedBazaarFlip { PlayerUuid = Player, ItemTag = "COAL", Amount = 64, BuyPrice = 100, SellPrice = 160, Profit = 60, SoldAt = new DateTime(2026, 10, 31, 23, 59, 0, DateTimeKind.Utc) });
        await Drain(writer);
        var row = store.Flips.Single();
        row.Month.Should().Be(202610);
        (row.Amount, row.BuyPriceTotal, row.SellPriceTotal, row.Profit).Should().Be((64, 100L, 160L, 60L));
    }


    // ---- update-only write path ----

    [Test]
    public async Task UnchangedHeartbeatsCauseNeitherReadNorWrite()
    {
        var store = new MemoryStore();
        using var writer = Writer(store);
        await writer.Process(Obs(10, T0.AddMinutes(1), 64, "chat-setup").WithHash("x"));
        var reads = store.Reads;
        var writes = store.WriteLog.Count;
        for (var i = 2; i < 8; i++)
            await writer.Process(Obs(10, T0.AddMinutes(i), 64, "gui-full-resend").WithHash("x"));
        store.Reads.Should().Be(reads);
        store.WriteLog.Count.Should().Be(writes);
    }

    [Test]
    public async Task LastObservedIsRefreshedAtMostHourlyAndAlone()
    {
        var store = new MemoryStore();
        using var writer = Writer(store);
        await writer.Process(Obs(10, T0, 64, "chat-setup").WithHash("x"));
        store.WriteLog.Clear();
        await writer.Process(Obs(10, T0.AddMinutes(59), 64, "gui-full-resend").WithHash("x"));
        store.WriteLog.Should().BeEmpty();
        await writer.Process(Obs(10, T0.AddMinutes(61), 64, "gui-full-resend").WithHash("x"));
        store.WriteLog.Should().ContainSingle().Which.Columns.Should().Be(OrderColumns.LastObserved);
        store.WriteLog[0].Points.Should().BeEmpty();
    }

    [Test]
    public async Task ChangeWritesOnlyAdvancedColumnsAndNewPoints()
    {
        var store = new MemoryStore();
        using var writer = Writer(store);
        await writer.Process(Obs(10, T0.AddMinutes(1), 64, "chat-setup").WithHash("x"));
        store.WriteLog.Clear();
        await writer.Process(Obs(30, T0.AddMinutes(2)).WithHash("x"));
        var (cols, points) = store.WriteLog.Single();
        cols.HasFlag(OrderColumns.Filled).Should().BeTrue();
        cols.HasFlag(OrderColumns.Price).Should().BeFalse();
        cols.HasFlag(OrderColumns.Amount).Should().BeFalse();
        cols.HasFlag(OrderColumns.FirstSeen).Should().BeFalse();
        points.Keys.Should().Equal(T0.AddMinutes(2)); // only the new point, never the whole map
    }

    [Test]
    public async Task CacheMissReadsOnceAndKeepsFirstSeenAndFullFill()
    {
        var store = new MemoryStore();
        using (var first = Writer(store))
            await first.Process(Obs(64, T0.AddMinutes(5), 64, "chat-setup").WithHash("x"));
        var stored = store.Orders.Values.Single();
        var fullFill = stored.FullFillAt;
        fullFill.Should().NotBeNull();
        store.WriteLog.Clear();

        // restarted pod: empty cache, resend arrives from the GUI
        using var restarted = Writer(store);
        await restarted.Process(Obs(64, T0.AddMinutes(30), 64, "gui-full-resend").WithHash("x"));
        await restarted.Process(Obs(64, T0.AddMinutes(40), 64, "gui-full-resend").WithHash("x"));
        store.Reads.Should().Be(1);
        store.WriteLog.Should().BeEmpty();
        stored.FirstSeenSource.Should().Be("chat-setup");
        stored.FullFillAt.Should().Be(fullFill);
    }

    [Test]
    public void NewRowNeverWritesNullColumns()
    {
        var after = BazaarOrderHistoryMerge.Apply(null, Obs(1, T0));
        var (cols, _) = BazaarOrderHistoryMerge.Diff(null, after);
        cols.HasFlag(OrderColumns.Claimed).Should().BeFalse();
        cols.HasFlag(OrderColumns.ClosedAt).Should().BeFalse();
        cols.HasFlag(OrderColumns.FullFill).Should().BeFalse();
        cols.HasFlag(OrderColumns.Filled).Should().BeTrue();
    }

    [Test]
    public async Task SchemaFailureIsCachedAndNotRetriedInsideTheWindow()
    {
        var session = new Mock<global::Cassandra.ISession>();
        session.SetupGet(x => x.Keyspace).Returns("ks");
        session.Setup(x => x.ExecuteAsync(It.IsAny<global::Cassandra.IStatement>())).ThrowsAsync(new InvalidOperationException("no CREATE permission"));
        var store = new CassandraBazaarHistoryStore(session.Object, Options(), NullLogger<CassandraBazaarHistoryStore>.Instance);
        Func<Task> act = () => store.UpsertFlip(new BazaarFlipHistoryRow { ItemTag = "COAL", AccountHash = "h", SoldAt = T0 }, 10);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await act.Should().ThrowAsync<InvalidOperationException>();
        await act.Should().ThrowAsync<InvalidOperationException>();
        session.Verify(x => x.ExecuteAsync(It.IsAny<global::Cassandra.IStatement>()), Times.Once);
    }

    [Test]
    public void TtlDoesNotOverflow()
    {
        new BazaarHistoryOptions { TtlDays = int.MaxValue }.TtlSeconds.Should().Be(630_720_000);
        new BazaarHistoryOptions { TtlDays = 180 }.TtlSeconds.Should().Be(180 * 86400);
    }

    [Test]
    public async Task EmitResolvedSkipsTagLookupWhenNothingConsumesTheEvent()
    {
        var items = new Mock<IItemsApi>();
        var state = new StateObject();
        state.McInfo.Uuid = Player;
        var args = new MockedUpdateArgs { currentState = state, msg = new() { UserId = "u", ReceivedAt = T0 } };
        args.AddService(items.Object);
        await BazaarOrderLifecycle.EmitResolved(args, new Offer { ItemName = "Coal", Amount = 1, Created = T0 }, "chat-setup", "Open");
        items.VerifyNoOtherCalls();
    }

    [Test]
    public async Task DisabledReaderAnswers503WithoutTouchingTheStore()
    {
        var store = new MemoryStore();
        var c = new BazaarHistoryController(new BazaarHistoryReader(store, Options(salt: null)));
        StatusOf(await c.GetOrders("COAL", T0, T0.AddDays(1))).Should().Be(503);
        StatusOf(await c.GetFlips("COAL", T0, T0.AddDays(1))).Should().Be(503);
        store.Queries.Should().BeEmpty();
    }

    [Test]
    public async Task CursorFromCallWithoutToKeepsWorking()
    {
        var store = new MemoryStore();
        BazaarOrderHistoryRow R(int i) => new() { ItemTag = "COAL", AccountHash = "h", Created = DateTime.UtcNow.AddMinutes(-i), State = "Open", FirstSeenSource = "x" };
        store.OrderPages = (p, size) => new HistoryPage<BazaarOrderHistoryRow> { Rows = Enumerable.Range(0, size).Select(R).ToList(), NextPagingState = p.EndsWith("+") ? null : new byte[] { 1 } };
        var reader = new BazaarHistoryReader(store, Options());
        var first = await reader.GetOrders("COAL", null, null, 2, null);
        await Task.Delay(20); // "now" moves on between the pages
        var second = await reader.GetOrders("COAL", null, null, 2, first.NextPagingState);
        second.Items.Should().HaveCount(2);
    }

    [Test]
    public async Task MalformedCursorStateIs400Not500()
    {
        var reader = new BazaarHistoryReader(new MemoryStore(), Options());
        string Cursor(string s) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
            $"{{\"I\":\"COAL\",\"F\":{T0.Ticks},\"T\":{T0.AddHours(1).Ticks},\"P\":0,\"S\":\"{s}\"}}"));
        var act = () => reader.GetOrders("COAL", null, null, 10, Cursor("***not-base64***"));
        (await act.Should().ThrowAsync<HistoryRequestException>()).Which.Status.Should().Be(400);
    }

    // ---- endpoints ----

    private static BazaarHistoryController Controller(MemoryStore store, int concurrency = 4) =>
        new(new BazaarHistoryReader(store, Options(readConcurrency: concurrency)));

    private static int StatusOf(ActionResult<HistoryResult<BazaarOrderHistoryDto>> r) => (r.Result as ObjectResult)?.StatusCode ?? 200;
    private static int StatusOf(ActionResult<HistoryResult<BazaarFlipHistoryDto>> r) => (r.Result as ObjectResult)?.StatusCode ?? 200;

    [Test]
    public async Task OrdersRejectSpanOverSevenDaysAndBadInputs()
    {
        var c = Controller(new MemoryStore());
        StatusOf(await c.GetOrders("COAL", T0, T0.AddDays(7).AddSeconds(1))).Should().Be(400);
        StatusOf(await c.GetOrders("COAL", T0, T0.AddDays(7))).Should().Be(200);
        StatusOf(await c.GetOrders(null, T0, T0.AddDays(1))).Should().Be(400);
        StatusOf(await c.GetOrders("COAL", T0.AddDays(1), T0)).Should().Be(400);
        StatusOf(await c.GetOrders("COAL", T0, T0.AddDays(1), 0)).Should().Be(400);
        StatusOf(await c.GetOrders("COAL", T0, T0.AddDays(1), 1001)).Should().Be(400);
        StatusOf(await c.GetOrders("COAL", T0, T0.AddDays(1), 1000)).Should().Be(200);
        StatusOf(await c.GetOrders("COAL", T0, T0.AddDays(1), 10, "garbage!")).Should().Be(400);
    }

    [Test]
    public async Task FlipsRejectSpanOverThirtyOneDays()
    {
        var c = Controller(new MemoryStore());
        StatusOf(await c.GetFlips("COAL", T0, T0.AddDays(31).AddSeconds(1))).Should().Be(400);
        StatusOf(await c.GetFlips("COAL", T0, T0.AddDays(31))).Should().Be(200);
        StatusOf(await c.GetFlips("COAL", T0, T0.AddDays(1), 1001)).Should().Be(400);
    }

    [Test]
    public async Task OrdersVisitOnlyCoveredDayPartitions()
    {
        var store = new MemoryStore();
        var c = Controller(store);
        await c.GetOrders("COAL", T0, T0.AddDays(2)); // Oct 20 23:59 .. Oct 22 23:59 -> 3 days
        store.Queries.Select(q => q.Partition).Should().Equal(0, 1, 2);
    }

    [Test]
    public async Task FlipsVisitMonthsNewestFirst()
    {
        var store = new MemoryStore();
        var c = Controller(store);
        await c.GetFlips("COAL", new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));
        store.Queries.Select(q => q.Partition).Should().Equal(202610, 202609);
    }

    [Test]
    public async Task PagingContinuesAcrossPartitionsWithCursor()
    {
        var store = new MemoryStore();
        BazaarOrderHistoryRow R(int i) => new() { ItemTag = "COAL", AccountHash = "h", Created = T0.AddMinutes(i), State = "Open", FirstSeenSource = "chat-setup" };
        store.OrderPages = (partition, size) => partition switch
        {
            "20261020" => new HistoryPage<BazaarOrderHistoryRow> { Rows = { R(1), R(2) } },
            "20261021" => new HistoryPage<BazaarOrderHistoryRow> { Rows = { R(3) }, NextPagingState = new byte[] { 1 } },
            "20261021+" => new HistoryPage<BazaarOrderHistoryRow> { Rows = { R(4) } },
            _ => new HistoryPage<BazaarOrderHistoryRow>()
        };
        var reader = new BazaarHistoryReader(store, Options());
        var first = await reader.GetOrders("COAL", T0, T0.AddDays(1), 3, null);
        first.Items.Should().HaveCount(3);
        first.NextPagingState.Should().NotBeNull();
        var second = await reader.GetOrders("COAL", T0, T0.AddDays(1), 3, first.NextPagingState);
        second.Items.Should().HaveCount(1);
        second.NextPagingState.Should().BeNull();
        // a cursor from another query is refused
        var act = () => reader.GetOrders("IRON_INGOT", T0, T0.AddDays(1), 3, first.NextPagingState);
        (await act.Should().ThrowAsync<HistoryRequestException>()).Which.Status.Should().Be(400);
    }

    [Test]
    public async Task SaturatedReaderAnswers429()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var store = new SlowQueryStore(entered, release.Task);
        var reader = new BazaarHistoryReader(store, Options(readConcurrency: 1));
        var running = reader.GetOrders("COAL", T0, T0.AddHours(1), null, null);
        await entered.Task;
        var act = () => reader.GetOrders("COAL", T0, T0.AddHours(1), null, null);
        (await act.Should().ThrowAsync<HistoryRequestException>()).Which.Status.Should().Be(429);
        release.SetResult();
        await running;
        (await reader.GetOrders("COAL", T0, T0.AddHours(1), null, null)).Items.Should().BeEmpty(); // slot freed again
    }

    private class SlowQueryStore : MemoryStore2
    {
        private readonly TaskCompletionSource entered;
        private readonly Task release;
        public SlowQueryStore(TaskCompletionSource entered, Task release) : base(Task.CompletedTask) { this.entered = entered; this.release = release; }
        public override async Task<HistoryPage<BazaarOrderHistoryRow>> QueryOrders(string itemTag, DateTime day, DateTime from, DateTime to, int pageSize, byte[]? pagingState)
        {
            entered.TrySetResult();
            await release;
            return new HistoryPage<BazaarOrderHistoryRow>();
        }
    }

    [Test]
    public void DtosHaveNoIdentityFields()
    {
        typeof(BazaarOrderHistoryDto).GetProperties().Select(p => p.Name.ToLowerInvariant())
            .Should().NotContain(n => n.Contains("uuid") || n.Contains("playername") || n == "name");
        typeof(BazaarFlipHistoryDto).GetProperties().Select(p => p.Name.ToLowerInvariant())
            .Should().NotContain(n => n.Contains("uuid") || n.Contains("playername") || n == "name");
    }
}

internal static class ObsExtensions
{
    public static BazaarOrderObservation WithHash(this BazaarOrderObservation o, string hash) { o.AccountHash = hash; return o; }
}
