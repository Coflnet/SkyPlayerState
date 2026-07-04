using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tests;

public class AchievementListenerTests
{
    private static MockedUpdateArgs ArgsFor(string achievementId, StateObject state)
    {
        var args = new MockedUpdateArgs
        {
            msg = new UpdateMessage { AchievementId = achievementId, PlayerId = "p" },
            currentState = state
        };
        args.AddService<IAchievementService>(new AchievementService());
        args.AddService<Microsoft.Extensions.Logging.ILogger<AchievementListener>>(NullLogger<AchievementListener>.Instance);
        return args;
    }

    [Test]
    public async Task UnlocksKnownAchievement()
    {
        var state = new StateObject();
        await new AchievementListener().Process(ArgsFor("FirstLowball", state));
        Assert.That(state.UnlockedAchievements, Does.Contain(Achievement.FirstLowball));
    }

    [Test]
    public async Task IgnoresUnknownAchievement()
    {
        var state = new StateObject();
        await new AchievementListener().Process(ArgsFor("SomethingRemovedLater", state));
        Assert.That(state.UnlockedAchievements, Is.Empty);
    }

    [Test]
    public async Task IgnoresEmptyId()
    {
        var state = new StateObject();
        // no service is needed - the listener must short circuit before touching anything
        var args = new MockedUpdateArgs { msg = new UpdateMessage { AchievementId = null, PlayerId = "p" }, currentState = state };
        await new AchievementListener().Process(args);
        Assert.That(state.UnlockedAchievements, Is.Empty);
    }
}
