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
}