using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class KatChatListenerTests
{
    [Test]
    public async Task UpgradeHoursAreReadAndLaterChatLinesStillProcess()
    {
        var args = new UpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage { ChatBatch = [
                "[NPC] Kat: I'll get your Bee upgraded to UNCOMMON in no time!",
                "[NPC] Kat: Come back in 2 days 4 hours to pick it up!"
            ] }
        };
        await new KatChatListener().Process(args);
        args.currentState.ExtractedInfo.KatStatus.KatEnd.Should()
            .BeCloseTo(DateTime.UtcNow.AddDays(2).AddHours(4), TimeSpan.FromSeconds(2));
    }
    [TestCase("")]
    [TestCase(".")]
    public async Task IsolatedReminderReadsMinutesWithOrWithoutFinalPeriod(string punctuation)
    {
        var args = new UpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage { ChatBatch = [
                "[NPC] Kat: You can pick it up in 59 minutes 22 seconds" + punctuation
            ] }
        };
        await new KatChatListener().Process(args);
        var status = args.currentState.ExtractedInfo.KatStatus;
        status.Should().NotBeNull();
        status.IsKatActive.Should().BeTrue();
        status.KatEnd.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(59).AddSeconds(22), TimeSpan.FromSeconds(2));
    }
}
