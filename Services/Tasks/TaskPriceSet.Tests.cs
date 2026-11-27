using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Bazaar.Client.Model;
using Coflnet.Sky.PlayerState.Controllers;
using Coflnet.Sky.Sniper.Client.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class TaskPriceSetTests
{
    private Mock<ISniperApi> sniper;
    private Mock<IBazaarApi> bazaar;

    [SetUp]
    public void Setup()
    {
        sniper = new Mock<ISniperApi>();
        bazaar = new Mock<IBazaarApi>();
    }

    private TaskPriceService Service(int minEntries = 0)
        => new(sniper.Object, bazaar.Object, NullLogger<TaskPriceService>.Instance) { MinSourceEntries = minEntries };

    private void BazaarReturns(params (string tag, double sell)[] items) => bazaar
        .Setup(b => b.GetAllPricesAsync(0, It.IsAny<CancellationToken>()))
        .ReturnsAsync(items.Select(i => new ItemPrice { ProductId = i.tag, SellPrice = i.sell }).ToList());

    private void CleanReturns(params (string tag, long price)[] items) => sniper
        .Setup(s => s.ApiSniperPricesCleanGetAsync(0, It.IsAny<CancellationToken>()))
        .ReturnsAsync(items.ToDictionary(i => i.tag, i => i.price));

    private void BazaarFails() => bazaar.Setup(b => b.GetAllPricesAsync(0, It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("down"));
    private void CleanFails() => sniper.Setup(s => s.ApiSniperPricesCleanGetAsync(0, It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("down"));

    [Test]
    public async Task BazaarSellPriceZero_FallsBackToCleanPrice()
    {
        BazaarReturns(("A", 0));
        CleanReturns(("A", 400));

        (await Service().GetPrices())["A"].Should().Be(400);
    }

    [Test]
    public async Task CleanPrice_DoesNotOverrideBazaarAndCopperIsSet()
    {
        CopperValue.Set(new CopperValueInfo { CoinsPerCopper = 9, IsFallback = false });
        BazaarReturns(("A", 10));
        CleanReturns(("A", 400));

        var prices = await Service().GetPrices();

        prices["A"].Should().Be(10);
        prices[PseudoItems.COPPER].Should().Be(9);
    }

    [Test]
    public async Task FailedSource_KeepsItsLastGoodResult()
    {
        BazaarReturns(("B", 10));
        CleanReturns(("A", 400));
        var service = Service();
        await service.GetPriceSet();
        BazaarFails();
        service.InvalidateCache();

        var set = await service.GetPriceSet();

        set.Prices.Should().Contain(new KeyValuePair<string, double>("B", 10)).And.Contain(new KeyValuePair<string, double>("A", 400));
        set.IsComplete.Should().BeTrue();
        set.CacheTtl.Should().Be(TaskPriceService.DegradedCacheDuration, "the failing source is retried soon");
    }

    [Test]
    public async Task SourceMissingWithoutLastGood_IsIncompleteAndCachedShort()
    {
        BazaarFails();
        CleanReturns(("A", 400));

        var set = await Service().GetPriceSet();

        set.IsComplete.Should().BeFalse();
        set.CacheTtl.Should().Be(TaskPriceService.DegradedCacheDuration);
        set.Prices["A"].Should().Be(400);
    }

    [Test]
    public async Task CompleteResult_UsesTheNormalTtl()
    {
        BazaarReturns(("B", 10));
        CleanReturns(("A", 400));

        (await Service().GetPriceSet()).CacheTtl.Should().Be(TaskPriceService.CacheDuration);
    }

    [Test]
    public async Task CallersDuringRefresh_AwaitItInsteadOfGettingAnEmptyResult()
    {
        BazaarReturns(("B", 10));
        sniper.Setup(s => s.ApiSniperPricesCleanGetAsync(0, It.IsAny<CancellationToken>()))
            .Returns(async () => { await Task.Delay(500); return new Dictionary<string, long> { { "A", 400 } }; });
        var service = Service();

        var sets = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => service.GetPriceSet()));

        sets.Should().OnlyContain(s => s.Prices.ContainsKey("A") && s.IsComplete);
        sniper.Verify(s => s.ApiSniperPricesCleanGetAsync(0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SniperPrices_AreSmoothedOverFetches()
    {
        BazaarReturns();
        var service = Service();
        foreach (var price in new long[] { 12_000, 990_000, 12_000 })
        {
            CleanReturns(("F3", price));
            service.InvalidateCache();
            await service.GetPriceSet();
        }

        (await service.GetPrices())["F3"].Should().Be(12_000);
    }

    [Test]
    public async Task Fingerprint_CountsTagsAndChangesWithThePrices()
    {
        BazaarReturns();
        CleanReturns(("A", 1), ("B", 2));
        var service = Service();
        var first = (await service.GetPriceSet()).Fingerprint;
        CleanReturns(("A", 1), ("B", 2), ("C", 3));
        service.InvalidateCache();
        var second = (await service.GetPriceSet()).Fingerprint;

        first.TagCount.Should().Be(3, "A, B and copper");
        first.Hash.Should().NotBe(second.Hash);
    }

    [Test]
    public async Task CommunityEstimates_AnswerNoStore503WhenPricesAreIncomplete()
    {
        BazaarFails();
        CleanFails();
        var controller = new TaskController(null!, null!, null!, Service(), null!, null!, new TaskRegistry(), NullLogger<TaskController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.GetCommunityEstimates(CancellationToken.None);

        result.Result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(503);
        controller.Response.Headers.CacheControl.ToString().Should().Contain("no-store");
    }

    [Test]
    public async Task ListenerFold_IsSkippedWhenNoPricesAreAvailable()
    {
        BazaarFails();
        CleanFails();
        var args = new Tests.MockedUpdateArgs { currentState = new Models.StateObject() };
        args.AddService(Service());
        args.AddService<Microsoft.Extensions.Logging.ILogger<Services.CollectionListener>>(NullLogger<Services.CollectionListener>.Instance);
        var period = new Services.TrackedProfitService.Period { DetectedTask = "SomeTask", PlayerUuid = "p" };

        // no TaskPeriodFolder is registered: reaching the fold would throw
        var prices = await new Services.CollectionListener().FoldFlush(args, period, null);

        Services.CollectionListener.HasPrices(prices).Should().BeFalse();
    }

    [Test]
    public async Task EmptyBazaarAnswer_KeepsLastGoodAndIsDegraded()
    {
        BazaarReturns(("B", 10));
        CleanReturns(("A", 400));
        var service = Service(minEntries: 1);
        await service.GetPriceSet();
        BazaarReturns();
        service.InvalidateCache();

        var set = await service.GetPriceSet();

        set.Prices["B"].Should().Be(10, "SkyBazaar answers [] before its first pull after a restart");
        set.CacheTtl.Should().Be(TaskPriceService.DegradedCacheDuration);
    }

    [Test]
    public async Task EmptyAnswerOnColdStart_IsIncomplete()
    {
        BazaarReturns();
        CleanReturns(("A", 400));

        (await Service(minEntries: 1).GetPriceSet()).IsComplete.Should().BeFalse();
    }

    [Test]
    public async Task ExpiredSet_IsServedAtOnceWhileRefreshingInTheBackground()
    {
        BazaarReturns(("B", 10));
        CleanReturns(("A", 400));
        var service = Service();
        var first = await service.GetPriceSet();
        var release = new TaskCompletionSource();
        sniper.Setup(s => s.ApiSniperPricesCleanGetAsync(0, It.IsAny<CancellationToken>()))
            .Returns(async () => { await release.Task; return new Dictionary<string, long> { { "A", 400 }, { "C", 1 } }; });
        service.MarkStale();

        var stale = await service.GetPriceSet().WaitAsync(TimeSpan.FromSeconds(1));
        release.SetResult();

        stale.Should().BeSameAs(first);
        await WaitUntil(async () => (await service.GetPriceSet()).Prices.ContainsKey("C"));
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("condition not met within 5 s");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task ThrowingRefresh_KeepsThePreviousSetWithTheShortTtl()
    {
        BazaarReturns(("B", 10));
        CleanReturns(("A", 400));
        var service = Service();
        var first = await service.GetPriceSet();
        service.AfterBuild = _ => throw new InvalidOperationException("post-fetch failure");
        service.MarkStale();

        await service.GetPriceSet();
        await WaitUntil(async () => (await service.GetPriceSet()).CacheTtl == TaskPriceService.DegradedCacheDuration);

        (await service.GetPriceSet()).Prices.Should().BeEquivalentTo(first.Prices);
    }

    [Test]
    public async Task ThrowingRefreshWithoutPreviousSet_IsIncompleteAndEmpty()
    {
        BazaarReturns(("B", 10));
        CleanReturns(("A", 400));
        var service = Service();
        service.AfterBuild = _ => throw new InvalidOperationException("post-fetch failure");

        var set = await service.GetPriceSet();

        set.IsComplete.Should().BeFalse();
        set.HasMarketPrices.Should().BeFalse();
        set.CacheTtl.Should().Be(TaskPriceService.DegradedCacheDuration);
    }

    [Test]
    public async Task IncompleteExistingSet_AwaitsTheRefreshInsteadOfBeingServed()
    {
        BazaarFails();
        CleanReturns(("A", 400));
        var service = Service();
        (await service.GetPriceSet()).IsComplete.Should().BeFalse();
        BazaarReturns(("B", 10));
        service.MarkStale();

        (await service.GetPriceSet()).IsComplete.Should().BeTrue();
    }

    [Test]
    public void SourceOlderThanSixHours_CountsAsMissing()
    {
        var now = DateTime.UtcNow;
        var bazaar = new List<ItemPrice> { new() { ProductId = "B", SellPrice = 10 } };
        var clean = new Dictionary<string, double> { { "A", 400 } };

        var set = TaskPriceService.Build(bazaar, now - TaskPriceService.MaxSourceAge - TimeSpan.FromMinutes(1), clean, now, true);

        set.IsComplete.Should().BeFalse();
        set.Prices.Should().NotContainKey("B");
    }

    [Test]
    public void FetchedAt_IsTheOlderSourceSuccess()
    {
        var now = DateTime.UtcNow;
        var bazaar = new List<ItemPrice> { new() { ProductId = "B", SellPrice = 10 } };
        var clean = new Dictionary<string, double> { { "A", 400 } };

        var set = TaskPriceService.Build(bazaar, now.AddHours(-2), clean, now, true);

        set.FetchedAt.Should().Be(now.AddHours(-2));
        set.Fingerprint.FetchedAt.Should().Be(now.AddHours(-2));
    }

    [Test]
    public async Task ListenerFold_IsSkippedWhenPricesAreIncomplete()
    {
        BazaarFails();
        CleanReturns(("A", 400));
        var args = new Tests.MockedUpdateArgs { currentState = new Models.StateObject() };
        args.AddService(Service());
        args.AddService<Microsoft.Extensions.Logging.ILogger<Services.CollectionListener>>(NullLogger<Services.CollectionListener>.Instance);
        var period = new Services.TrackedProfitService.Period { DetectedTask = "SomeTask", PlayerUuid = "p" };

        var prices = await new Services.CollectionListener().FoldFlush(args, period, null);

        Services.CollectionListener.HasPrices(prices).Should().BeFalse();
    }
}
