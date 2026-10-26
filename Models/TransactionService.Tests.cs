using System;
using System.Linq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Models;

public class TransactionServiceTests
{
    [Test]
    public void NormalizeTransactionsForStorageKeepsCoinPlaceholderForPlayerTransactions()
    {
        var playerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;

        var result = TransactionService.NormalizeTransactionsForStorage(new[]
        {
            new Transaction
            {
                PlayerUuid = playerId,
                ProfileUuid = profileId,
                ItemId = SpecialTransactionItemIds.Coins,
                Amount = 12345,
                TimeStamp = timestamp,
                Type = Transaction.TransactionType.BazaarListSell
            },
            new Transaction
            {
                PlayerUuid = playerId,
                ProfileUuid = profileId,
                ItemId = 5,
                Amount = 64,
                TimeStamp = timestamp,
                Type = Transaction.TransactionType.BazaarSell
            }
        });

        Assert.That(result.Select(t => t.ItemId).ToArray(), Is.EqualTo(new[] { SpecialTransactionItemIds.Coins, 5L }));
        Assert.That(result.Single(t => t.ItemId == SpecialTransactionItemIds.Coins).Amount, Is.EqualTo(12345));
        Assert.That(result.Single(t => t.ItemId == 5).Amount, Is.EqualTo(64));
    }

    [Test]
    public void ShouldStoreInItemTransactionsSkipsCoinPlaceholder()
    {
        Assert.That(TransactionService.ShouldStoreInItemTransactions(new Transaction { ItemId = SpecialTransactionItemIds.Coins }), Is.False);
        Assert.That(TransactionService.ShouldStoreInItemTransactions(new Transaction { ItemId = 5 }), Is.True);
    }

    [Test]
    public void NormalizeTransactionsForStorageAggregatesDuplicatesWithoutDroppingCoins()
    {
        var playerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var timestamp = DateTime.UtcNow;

        var result = TransactionService.NormalizeTransactionsForStorage(new[]
        {
            new Transaction
            {
                PlayerUuid = playerId,
                ProfileUuid = profileId,
                ItemId = 5,
                Amount = 2,
                TimeStamp = timestamp,
                Type = Transaction.TransactionType.BazaarSell
            },
            new Transaction
            {
                PlayerUuid = playerId,
                ProfileUuid = profileId,
                ItemId = 5,
                Amount = 3,
                TimeStamp = timestamp,
                Type = Transaction.TransactionType.BazaarSell
            },
            new Transaction
            {
                PlayerUuid = playerId,
                ProfileUuid = profileId,
                ItemId = SpecialTransactionItemIds.Coins,
                Amount = 999,
                TimeStamp = timestamp,
                Type = Transaction.TransactionType.BazaarListSell
            }
        });

        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Single(t => t.ItemId == 5).Amount, Is.EqualTo(5));
        Assert.That(result.Single(t => t.ItemId == SpecialTransactionItemIds.Coins).Amount, Is.EqualTo(999));
    }

    [TestCase(0, TransactionService.MaxItemTransactions)]
    [TestCase(-1, TransactionService.MaxItemTransactions)]
    [TestCase(int.MinValue, TransactionService.MaxItemTransactions)]
    [TestCase(TransactionService.MaxItemTransactions + 1, TransactionService.MaxItemTransactions)]
    [TestCase(int.MaxValue, TransactionService.MaxItemTransactions)]
    [TestCase(1, 1)]
    [TestCase(100, 100)]
    [TestCase(TransactionService.MaxItemTransactions, TransactionService.MaxItemTransactions)]
    public void ClampItemTransactionLimitBoundsReads(int requested, int expected)
    {
        Assert.That(TransactionService.ClampItemTransactionLimit(requested), Is.EqualTo(expected));
    }

    [Test]
    public void ItemQueryIsNewestFirstWithLimit()
    {
        var session = new Moq.Mock<global::Cassandra.ISession>();
        var cluster = new Moq.Mock<global::Cassandra.ICluster>();
        cluster.Setup(c => c.Configuration).Returns(global::Cassandra.Cluster.Builder().AddContactPoint("127.0.0.1").GetConfiguration());
        session.Setup(x => x.Cluster).Returns(cluster.Object);
        var table = TransactionService.GetItemTable(session.Object);
        var cql = TransactionService.BuildNewestItemTransactionsQuery(table, 5, 1000).ToString();
        Assert.That(cql, Does.Contain("ORDER BY").And.Contain("DESC").And.Contain("LIMIT"));
    }

    private static System.Collections.Generic.List<DateTime> Stamps(int count)
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count).Select(i => start.AddMinutes(-i)).ToList();
    }

    [Test]
    public void PruneCutoffIsNullAtOrBelowKeep()
    {
        Assert.That(TransactionService.GetPruneCutoff(Stamps(1000), 1000), Is.Null);
        Assert.That(TransactionService.GetPruneCutoff(Stamps(3), 1000), Is.Null);
    }

    [Test]
    public void PruneCutoffIsThe1001stRow()
    {
        var stamps = Stamps(1001);
        Assert.That(TransactionService.GetPruneCutoff(stamps, 1000), Is.EqualTo(stamps[1000]));
    }

    [Test]
    public void PruneDisabledWhenKeepIsZero()
    {
        Assert.That(TransactionService.GetPruneCutoff(Stamps(5000), 0), Is.Null);
        Assert.That(TransactionService.ShouldPrune(0, 0), Is.False);
    }

    [Test]
    public void PruneIsSampled()
    {
        Assert.That(TransactionService.ShouldPrune(1000, 0), Is.True);
        Assert.That(TransactionService.ShouldPrune(1000, 1), Is.False);
        Assert.That(TransactionService.ShouldPrune(1000, 49), Is.False);
    }

    [Test]
    public void TradeLookupKeepsUsingOldestTransfer()
    {
        var old = new Transaction { TimeStamp = DateTime.UtcNow.AddDays(-2), PlayerUuid = Guid.NewGuid() };
        var recent = new Transaction { TimeStamp = DateTime.UtcNow, PlayerUuid = Guid.NewGuid() };
        // newest first, as returned by GetItemTransactions
        Assert.That(TradeService.SelectOldestTransfer(new[] { recent, old }), Is.SameAs(old));
        Assert.That(TradeService.SelectOldestTransfer(Array.Empty<Transaction>()), Is.Null);
    }
}
