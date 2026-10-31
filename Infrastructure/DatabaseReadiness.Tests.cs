using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Cassandra;
using ISession = Cassandra.ISession;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Infrastructure;

public class DatabaseReadinessTests
{
    [Test]
    public async Task EachCheckQueriesOnlyTheMainSessionWithBoundedReadOnlyStatement()
    {
        var session = new Mock<ISession>();
        session.Setup(s => s.ExecuteAsync(It.IsAny<IStatement>())).ReturnsAsync(new RowSet());
        using var services = new ServiceCollection().AddSingleton(session.Object).BuildServiceProvider();
        await DatabaseReadiness.CheckAsync(services);
        await DatabaseReadiness.CheckAsync(services);
        session.Verify(s => s.ExecuteAsync(It.Is<SimpleStatement>(q =>
            q.QueryString == "SELECT key FROM system.local" && q.ReadTimeoutMillis == 3000 &&
            q.RetryPolicy is FallthroughRetryPolicy)), Times.Exactly(2));
        session.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SuccessfulFreshReadReturns200()
    {
        var session = new Mock<ISession>();
        session.Setup(s => s.ExecuteAsync(It.IsAny<IStatement>())).ReturnsAsync(new RowSet());
        using var services = new ServiceCollection().AddSingleton(session.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await DatabaseReadiness.HandleAsync(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(200));
    }

    [Test]
    public async Task DatabaseFailureReturns503RatherThan200WithFalseBody()
    {
        var session = new Mock<ISession>();
        session.Setup(s => s.ExecuteAsync(It.IsAny<IStatement>())).ThrowsAsync(new InvalidOperationException("synthetic"));
        using var services = new ServiceCollection().AddSingleton(session.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        await DatabaseReadiness.HandleAsync(context);
        Assert.That(context.Response.StatusCode, Is.EqualTo(503));
        Assert.That(context.Response.Body.Length, Is.Zero);
    }

    [Test]
    public void CurrentMainSessionInitializationFailureIsNotHidden()
    {
        using var services = new ServiceCollection().AddSingleton<ISession>(_ =>
            throw new InvalidOperationException("synthetic initializer")).BuildServiceProvider();
        Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReadiness.CheckAsync(services));
    }

    [Test]
    public async Task AStalledQueryHasOneFourSecondOverallDeadline()
    {
        var pending = new TaskCompletionSource<RowSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Mock<ISession>();
        session.Setup(s => s.ExecuteAsync(It.IsAny<IStatement>())).Returns(pending.Task);
        using var services = new ServiceCollection().AddSingleton(session.Object).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var elapsed = Stopwatch.StartNew();
        try
        {
            await DatabaseReadiness.HandleAsync(context);
            Assert.That(context.Response.StatusCode, Is.EqualTo(503));
            Assert.That(elapsed.Elapsed, Is.InRange(TimeSpan.FromSeconds(3.8), TimeSpan.FromSeconds(6)));
        }
        finally { pending.TrySetResult(new RowSet()); }
    }

    [Test]
    public async Task CallerCancellationStopsWaitingWithoutDisposingTheMainSession()
    {
        var pending = new TaskCompletionSource<RowSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new Mock<ISession>();
        session.Setup(s => s.ExecuteAsync(It.IsAny<IStatement>())).Returns(pending.Task);
        using var services = new ServiceCollection().AddSingleton(session.Object).BuildServiceProvider();
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = cancelled.Token };
        var elapsed = Stopwatch.StartNew();
        try
        {
            await DatabaseReadiness.HandleAsync(context);
            Assert.That(context.Response.StatusCode, Is.EqualTo(503));
            Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
            session.Verify(s => s.Dispose(), Times.Never);
        }
        finally { pending.TrySetResult(new RowSet()); }
    }
}
