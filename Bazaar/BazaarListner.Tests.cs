using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Coflnet.Sky.PlayerState.Tests;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Bazaar;

public class BazaarListnerTests
{
    [Test]
    public async Task Parse()
    {
        UpdateArgs args = GetArgs();
        var listener = new BazaarListener();
        await listener.Process(args);
        Assert.That(2, Is.EqualTo(args.currentState.BazaarOffers.Count));
        Assert.That(2640, Is.EqualTo(args.currentState.BazaarOffers[0].Amount));
        Assert.That(1820.9, Is.EqualTo(args.currentState.BazaarOffers[0].PricePerUnit));
        Assert.That(2, Is.EqualTo(args.currentState.BazaarOffers[0].Customers.Count));
        Assert.That(1501, Is.EqualTo(args.currentState.BazaarOffers[0].Customers[0].Amount));
        Assert.That("§b[MVP§2+§b] Terminator602", Is.EqualTo(args.currentState.BazaarOffers[0].Customers[0].PlayerName));
        Assert.That(139, Is.EqualTo(args.currentState.BazaarOffers[0].Customers[1].Amount));
        Assert.That("§a[VIP§6+§a] Luka_Daddy", Is.EqualTo(args.currentState.BazaarOffers[0].Customers[1].PlayerName));
        Assert.That(400, Is.EqualTo(args.currentState.BazaarOffers[1].Amount));
        Assert.That(25130.6, Is.EqualTo(args.currentState.BazaarOffers[1].PricePerUnit));
    }

    [Test]
    public async Task UpdateNotOverride()
    {
        UpdateArgs args = GetArgs();
        var time = new DateTime(2021, 1, 1) + TimeSpan.FromSeconds(Random.Shared.Next(1, 100000));
        var offer = new Offer()
        {
            Amount = 2640,
            ItemName = "Enchanted Rotten Flesh",
            PricePerUnit = 1820.9,
            IsSell = true,
            Created = time,
            Customers = new()
            {
                new()
                {
                    Amount = 1501,
                    PlayerName = "§b[MVP§2+§b] Terminator602",
                    TimeStamp = time
                },
            }
        };
        var offers = new List<Offer>()
        {
            offer,
            offer
        };
        args.currentState.BazaarOffers = offers;
        var listener = new BazaarListener();
        await listener.Process(args);
        var stored = args.currentState.BazaarOffers.First();
        Assert.That(BazaarListener.OrderKey(stored), Is.EqualTo(BazaarListener.OrderKey(offer)));
        Assert.That(stored.Created, Is.EqualTo(offer.Created));
        Assert.That(stored.Customers, Has.Count.EqualTo(2));
        Assert.That(stored.Customers[0].TimeStamp, Is.EqualTo(time), 
            "Customer timestamp should not be updated as previous time is more exact");
    }

    [Test]
    public async Task IgnoresNullHistoricalOffers()
    {
        var args = GetArgs();
        args.currentState.BazaarOffers.Add(null!);

        await new BazaarListener().Process(args);

        Assert.That(args.currentState.BazaarOffers, Has.Count.EqualTo(2));
    }

    private static UpdateArgs GetArgs()
    {
        var args = new MockedUpdateArgs()
        {
            currentState = new(),
            msg = new UpdateMessage()
            {
                Chest = new ChestView()
                {
                    Name = "Your Bazaar Orders",
                    Items = new List<Item>(){
                        new Item(){
                            ItemName = "§6§lSELL §aEnchanted Rotten Flesh",
                            Description = """
§Worth 519k coins

§7Offer amount: §a2,640§7x
§7Filled: §6640§7/640 §a§l100%!

§8Expired!

§7Price per unit: §61,820.9 coins

§7Customers:
§8- §a1,501§7x §b[MVP§2+§b] Terminator602§f §819d ago
§8- §a139§7x §a[VIP§6+§a] Luka_Daddy§f §819d ago

§eYou have §6519,466 coins §eto claim!

§eClick to claim!
""",
Tag = "ROTTEN_FLESH"

                        },
                        new Item(){
                            ItemName="§a§lBUY §aWorm Membrane",
                            Description="§8Worth 10M coins\n\n§7Order amount: §a400§7x\n\n§7Price per unit: §625,130.6 coins\n\n§eClick to view options!"
                        }
                    }
                }
            }
        };
        args.AddService<ILogger<BazaarListener>>(NullLogger<BazaarListener>.Instance);
        return args;
    }
}
