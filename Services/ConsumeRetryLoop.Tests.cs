using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class ConsumeRetryLoopTests
{
    private static async Task YieldDelay(TimeSpan _, CancellationToken __) => await Task.Yield();

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    [Test]
    public void Backoff_DoublesFromTwoSecondsToSixtyAndResets()
    {
        var b = new ConsumeBackoff();
        var waits = Enumerable.Range(0, 7).Select(_ => b.Next().TotalSeconds).ToList();
        Assert.That(waits, Is.EqualTo(new double[] { 2, 4, 8, 16, 32, 60, 60 }));
        b.Reset();
        Assert.That(b.Next().TotalSeconds, Is.EqualTo(2));
    }

    [Test]
    public async Task Exception_IsRetriedWithGrowingBackoff()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var delays = new List<TimeSpan>();
        await ConsumeRetryLoop.RunAsync(
            () =>
            {
                if (++attempts < 3) throw new InvalidOperationException("preflight");
                cts.Cancel();
                return Task.CompletedTask;
            },
            new ConsumeBackoff(),
            (_, _) => { },
            (d, _) => { delays.Add(d); return Task.CompletedTask; },
            cts.Token);

        Assert.That(attempts, Is.EqualTo(3));
        Assert.That(delays.Select(d => d.TotalSeconds), Is.EqualTo(new double[] { 2, 4 }));
    }

    [Test]
    public async Task NormalReturnWithoutShutdown_IsRetriedAsFailure()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var failures = new List<Exception>();
        await ConsumeRetryLoop.RunAsync(
            () =>
            {
                if (++attempts == 2) cts.Cancel();
                return Task.CompletedTask;
            },
            new ConsumeBackoff(),
            (ex, _) => failures.Add(ex),
            NoDelay,
            cts.Token);

        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0], Is.TypeOf<InvalidOperationException>());
    }

    [Test]
    public async Task ResetAfterProcessedBatch_RestartsBackoffAtTwoSeconds()
    {
        using var cts = new CancellationTokenSource();
        var backoff = new ConsumeBackoff();
        var attempts = 0;
        var delays = new List<TimeSpan>();
        await ConsumeRetryLoop.RunAsync(
            () =>
            {
                attempts++;
                if (attempts == 3) backoff.Reset(); // a batch was processed before this failure
                if (attempts == 4) cts.Cancel();
                throw new InvalidOperationException("boom");
            },
            backoff,
            (_, _) => { },
            (d, _) => { delays.Add(d); return Task.CompletedTask; },
            cts.Token);

        Assert.That(delays.Select(d => d.TotalSeconds), Is.EqualTo(new double[] { 2, 4, 2 }));
    }

    [Test]
    public async Task Cancellation_ExitsQuietlyWithoutReportingFailure()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        var failureReported = false;
        await ConsumeRetryLoop.RunAsync(
            () => { attempts++; cts.Cancel(); throw new OperationCanceledException(); },
            new ConsumeBackoff(),
            (_, _) => failureReported = true,
            NoDelay,
            cts.Token);

        Assert.That(attempts, Is.EqualTo(1));
        Assert.That(failureReported, Is.False);
    }

    [Test]
    public async Task CancellationDuringBackoffDelay_ExitsQuietly()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        await ConsumeRetryLoop.RunAsync(
            () => { attempts++; throw new InvalidOperationException("boom"); },
            new ConsumeBackoff(),
            (_, _) => { },
            (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            cts.Token);

        Assert.That(attempts, Is.EqualTo(1));
    }

    [Test]
    public async Task OneConsumerFailing_DoesNotStopTheOthers()
    {
        using var cts = new CancellationTokenSource();
        var healthyRuns = 0;
        var failingRuns = 0;
        var failing = ConsumeRetryLoop.RunAsync(
            () => { failingRuns++; throw new InvalidOperationException("preflight"); },
            new ConsumeBackoff(), (_, _) => { }, YieldDelay, cts.Token);
        var healthy = ConsumeRetryLoop.RunAsync(
            () => { if (++healthyRuns == 5) cts.Cancel(); return Task.CompletedTask; },
            new ConsumeBackoff(), (_, _) => { }, YieldDelay, cts.Token);

        await Task.WhenAll(failing, healthy);

        Assert.That(healthyRuns, Is.EqualTo(5));
        Assert.That(failingRuns, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void FatalException_PropagatesInsteadOfRetrying()
    {
        var attempts = 0;
        var ex = Assert.ThrowsAsync<TimeoutException>(() => ConsumeRetryLoop.RunAsync(
            () => { attempts++; throw new TimeoutException("while lagging; restarting consumer host"); },
            new ConsumeBackoff(), (_, _) => { }, NoDelay, CancellationToken.None,
            PlayerStateBackgroundService.IsPartitionStall));

        Assert.That(attempts, Is.EqualTo(1));
        Assert.That(ex.Message, Does.Contain("while lagging"));
    }

    [Test]
    public async Task NonFatalException_IsStillRetriedWhenFatalPredicateGiven()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;
        await ConsumeRetryLoop.RunAsync(
            () =>
            {
                if (++attempts == 3) cts.Cancel();
                throw attempts == 1 ? new InvalidOperationException("handoff") : new TimeoutException("assignment timeout");
            },
            new ConsumeBackoff(), (_, _) => { }, NoDelay, cts.Token,
            PlayerStateBackgroundService.IsPartitionStall);

        Assert.That(attempts, Is.EqualTo(3));
    }
}
