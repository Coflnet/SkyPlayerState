using System;
using System.Collections.Generic;
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

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarOrderSyncTests
{
    private class Backend(bool legacy) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri.AbsolutePath, await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(legacy && request.RequestUri.AbsolutePath == "/OrderBook/player"
                ? HttpStatusCode.NotFound : HttpStatusCode.NoContent);
        }
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
