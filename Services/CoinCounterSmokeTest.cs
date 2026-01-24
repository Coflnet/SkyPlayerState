using System;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Moq;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tests;

[TestFixture]
public class CoinCounterSmokeTest
{
    [Test]
    public async Task SimulateMessageAndVerifyCounter()
    {
        // Arrange
        var loggerMock = new Mock<ILogger<CoinCounterListener>>();
        var counterServiceMock = new Mock<ICoinCounterService>();
        var listener = new CoinCounterListener(counterServiceMock.Object);
        
        var message = new UpdateMessage
        {
            PlayerId = "test_user",
            UserId = "test_user",
            ReceivedAt = DateTime.UtcNow,
            Kind = UpdateMessage.UpdateKind.CHAT,
            ChatBatch = new List<string>
            {
                "You sold Enchanted Snow Block x1 for 600 Coins!",
                "[Bazaar] Your Enchanted Snow Block x10 sold for 6,000 coins!",
                " + 2k coins"
            }
        };
        
        var state = new StateObject
        {
            PlayerId = "test_user"
        };
        
        var args = new UpdateArgs
        {
            msg = message,
            currentState = state
        };
        
        // Act
        await listener.Process(args);
        
        // Assert - verify the service was called 3 times with correct values
        counterServiceMock.Verify(x => x.IncrementCounter(
            "test_user",
            It.IsAny<DateTime>(),
            CoinCounterType.Npc,
            6000L), Times.Once);
            
        counterServiceMock.Verify(x => x.IncrementCounter(
            "test_user",
            It.IsAny<DateTime>(),
            CoinCounterType.Bazaar,
            60000L), Times.Once);
            
        counterServiceMock.Verify(x => x.IncrementCounter(
            "test_user",
            It.IsAny<DateTime>(),
            CoinCounterType.Trade,
            20000L), Times.Once);
    }
}
