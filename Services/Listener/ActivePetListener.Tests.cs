using Coflnet.Sky.PlayerState.Models;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using FluentAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class ActivePetListenerTests
{
    [Test]
    public void ParseActivePet_ExtractsNameAndProgress()
    {
        var listener = new ActivePetListener();
        var item = new Item
        {
            ItemName = "§a§aPets",
            Description = "§7§7View and manage all of your Pets.\n" +
                          "\n" +
                          "§7§7Level up your pets faster by gaining\n" +
                          "§7XP in their favorite skill!\n" +
                          "\n" +
                          "§7§7Selected pet: §6Grandma Wolf\n" +
                          "\n" +
                          "§7Progress to Level 99: §e88.9%\n" +
                          "§2§l§m                       §f§l§m  §e1,552,069.9§6/§e1.7M"
        };

        var result = listener.ParseActivePet(item);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Grandma Wolf");
        result.ColorCode.Should().Be("§6");
        result.TargetLevel.Should().Be(99);
        result.ProgressPercent.Should().BeApproximately(88.9, 0.001);
    }

        [Test]
        public void Process_ExtractsAllPetsAndLevels()
        {
            var listener = new ActivePetListener();
            var args = new UpdateArgs
            {
                msg = new UpdateMessage
                {
                    Chest = new ChestView
                    {
                        Name = "Pets",
                        Items = new List<Item>
                        {
                            new Item
                            {
                                ItemName = "§6[Lvl 50] Grandma Wolf",
                                Tag = "PET_GRANDMA_WOLF",
                                Description = "§7Progress to Level 51: §e50.0%\n§e50000/§e100000",
                                ExtraAttributes = new Dictionary<string, object>
                                {
                                    { "petInfo", Newtonsoft.Json.Linq.JObject.FromObject(new {
                                        type = "GRANDMA_WOLF",
                                        tier = "LEGENDARY",
                                        exp = 12345.0,
                                        active = true,
                                        heldItem = "None",
                                        candyUsed = 0,
                                        uuid = "uuid-1"
                                    }) }
                                }
                            },
                            new Item
                            {
                                ItemName = "§b[Lvl 30] Rock",
                                Tag = "PET_ROCK",
                                Description = "§7Progress to Level 31: §e10.0%\n§e10000/§e100000",
                                ExtraAttributes = new Dictionary<string, object>
                                {
                                    { "petInfo", Newtonsoft.Json.Linq.JObject.FromObject(new {
                                        type = "ROCK",
                                        tier = "EPIC",
                                        exp = 54321.0,
                                        active = false,
                                        heldItem = "None",
                                        candyUsed = 2,
                                        uuid = "uuid-2"
                                    }) }
                                }
                            }
                        }
                    }
                },
                currentState = new StateObject { ExtractedInfo = new ExtractedInfo() }
            };

            listener.Process(args).Wait();

            var pets = args.currentState.ExtractedInfo.Pets;
            pets.Should().NotBeNull();
            pets.Should().HaveCount(2);
            pets![0].Name.Should().Be("[Lvl 50] Grandma Wolf");
            pets[0].Type.Should().Be("GRANDMA_WOLF");
            pets[0].Tier.Should().Be("LEGENDARY");
            pets[0].Level.Should().Be(50);
            pets[0].IsActive.Should().BeTrue();
            pets[0].ProgressPercent.Should().BeApproximately(50.0, 0.001);
            pets[0].TargetLevel.Should().Be(51);
            pets[0].CurrentExp.Should().BeApproximately(50000, 0.001);
            pets[0].ExpForLevel.Should().BeApproximately(100000, 0.001);
            pets[0].Uuid.Should().Be("uuid-1");

            pets[1].Name.Should().Be("[Lvl 30] Rock");
            pets[1].Type.Should().Be("ROCK");
            pets[1].Tier.Should().Be("EPIC");
            pets[1].Level.Should().Be(30);
            pets[1].IsActive.Should().BeFalse();
            pets[1].ProgressPercent.Should().BeApproximately(10.0, 0.001);
            pets[1].TargetLevel.Should().Be(31);
            pets[1].CurrentExp.Should().BeApproximately(10000, 0.001);
            pets[1].ExpForLevel.Should().BeApproximately(100000, 0.001);
            pets[1].Uuid.Should().Be("uuid-2");

            // ActivePet should match the active pet
            var activePet = args.currentState.ExtractedInfo.ActivePet;
            activePet.Should().NotBeNull();
            activePet!.Name.Should().Be("[Lvl 50] Grandma Wolf");
            activePet.ColorCode.Should().Be("§6");
            activePet.TargetLevel.Should().Be(51);
            activePet.ProgressPercent.Should().BeApproximately(50.0, 0.001);
        }

    [Test]
    public void ParseActivePet_WhenNoneSelected_ReturnsNull()
    {
        var listener = new ActivePetListener();
        var item = new Item
        {
            ItemName = "§a§aPets",
            Description = "§7§7Selected pet: §cNone\n"
        };

        var result = listener.ParseActivePet(item);

        result.Should().BeNull();
    }
}
