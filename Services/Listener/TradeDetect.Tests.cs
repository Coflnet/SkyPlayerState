using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Castle.Core.Logging;
using Coflnet.Sky.Core;
using Coflnet.Sky.PlayerName.Client.Api;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class TradeDetectTests
{
    private static string Inventory = """
    {"Kind":2,"ReceivedAt":"2023-11-19T12:34:38.0568064Z","Chest":{"Items":[{"Id":null,"ItemName":"§fRaw Beef","Tag":"RAW_BEEF","ExtraAttributes":{"tier":1},"Enchantments":null,"Color":null,"Description":"§f§lCOMMON","Count":13},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":"§7⇦ Your stuff","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"§7Their stuff ⇨","Count":1},{"Id":null,"ItemName":"§62k coins","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"§7Lump-sum amount\n\n§6Total Coins Offered:\n§72k\n§8(2,000)","Count":1},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":"§7⇦ Your stuff","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"§7Their stuff ⇨","Count":1},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":"§7⇦ Your stuff","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"§7Their stuff ⇨","Count":1},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":"§7⇦ Your stuff","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"§7Their stuff ⇨","Count":1},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":"§6Coins transaction","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"\n§7Daily limit: 10k§7/50M\n\n§eClick to add gold!","Count":1},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":"§aDeal accepted!","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"§7You accepted the trade.\n§7Wait for the other party to\n§7accept.","Count":1},{"Id":null,"ItemName":"§7⇦ Your stuff","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"§7Their stuff ⇨","Count":1},{"Id":null,"ItemName":"§ePending their confirm","Tag":null,"ExtraAttributes":null,"Enchantments":null,"Color":null,"Description":"§7Trading with §7VakarisRu§7§7.\n§7Waiting for them to confirm...","Count":1},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":null,"Tag":null,"ExtraAttributes":null,"Enchantments":{},"Color":null,"Description":null,"Count":0},{"Id":null,"ItemName":"§aSkyBlock Menu §7(Click)","Tag":"SKYBLOCK_MENU","ExtraAttributes":{},"Enchantments":null,"Color":null,"Description":"§7View all of your SkyBlock\n§7progress, including your Skills,\n§7Collections, Recipes, and more!\n\n§eClick to open!","Count":1}],"Name":"You                  VakarisRu"},"ChatBatch":null,"PlayerId":"Ekwav","SessionId":"c6728a74-d31d-48ca-aef4-7c0aad2056cb"}
    """;

    private static string ChatUpdate = """{"Kind":1,"ReceivedAt":"2023-11-19T12:34:39.5832977Z","Chest":null,"ChatBatch":["Trade completed with VakarisRu!"," + 2k coins"," - 13x Raw Beef"],"PlayerId":"Ekwav","SessionId":"KXZ2q2ifkCBlthrFrcdUzg=="}""";

    [Test]
    public async Task TriggersTrades()
    {
        // console logger for ILogger<TradeDetect>
        var logger = LoggerFactory.Create(b => {
            //b.AddConsole();
        }).CreateLogger<TradeDetect>();
        var service = new TradeDetect(logger);
        var inventory = JsonConvert.DeserializeObject<UpdateMessage>(Inventory)!.Chest;
        var args = GetUpdateArgs(ChatUpdate);
        var transactionService = new Mock<ITransactionService>();
        var nameService = new Mock<IPlayerNameApi>();
        var itemDetails = new ItemDetails(null);
        nameService.Setup(s => s.PlayerNameUuidNameGetAsync(It.IsAny<string>(), 0, default)).ReturnsAsync(Guid.Empty.ToString());
        args.AddService(transactionService.Object);
        args.AddService(nameService.Object);
        args.AddService(itemDetails);
        args.currentState.RecentViews.Enqueue(inventory);
        var beefId = Random.Shared.Next(10, 10000);
        itemDetails.TagLookup["RAW_BEEF"] = beefId;

        await service.Process(args);

        transactionService.Verify(t => t.AddTransactions(It.Is<IEnumerable<Transaction>>(t => 
            t.Count() == 4 
            && t.First().ItemId == beefId
            && t.Last().ItemId == TradeDetect.IdForCoins && t.Last().Amount == 20000
        )), Times.Once);
    }

    private MockedUpdateArgs GetUpdateArgs(string json)
    {
        var msg = JsonConvert.DeserializeObject<UpdateMessage>(json)!;
        var args = new MockedUpdateArgs()
        {
            currentState = new StateObject()
            {
                RecentViews = new(),
                ChatHistory = new()
            },
            msg = msg
        };
        var stateService = new Mock<IPlayerStateService>();
        args.stateService = stateService.Object;
        return args;
    }
}
