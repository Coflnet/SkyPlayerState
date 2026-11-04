using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class ProfileSwitchListenerTests
{
    private const string IdText = "3c6c1a07-1f4b-4b0c-9a5e-0d6a2d7b9f10";
    private static readonly Guid Id = Guid.Parse(IdText);

    [TestCase("Profile ID: " + IdText, true)]
    [TestCase("§aProfile ID: §7" + IdText, true)]
    [TestCase("Profile ID: not-a-uuid", false)]
    [TestCase("Profile ID: 00000000-0000-0000-0000-000000000000", false)]
    [TestCase("hello Profile ID: " + IdText, false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void ParsesProfileId(string line, bool valid)
    {
        ProfileSwitchListener.ParseProfileId(line).HasValue.Should().Be(valid);
    }

    [TestCase("You are playing on profile: Banana", "Banana")]
    [TestCase("§eYou are playing on profile: §aCucumber§b♲", "Cucumber")]
    [TestCase("You are playing on profile: Lemon Ⓑ", "Lemon")]
    [TestCase("You are playing on server", null)]
    [TestCase(null, null)]
    public void ParsesChatProfileName(string line, string expected)
    {
        ProfileSwitchListener.ParseChatProfileName(line).Should().Be(expected);
    }

    [Test]
    public void ParsesTabProfileName()
    {
        var tab = new[] { null, "§b§lArea: Hub", "§a§lProfile: §e§aPear♲", "Purse: 5" };
        ProfileSwitchListener.ParseTabProfileName(tab).Should().Be("Pear");
        ProfileSwitchListener.ParseTabProfileName(new[] { "Area: Hub" }).Should().BeNull();
    }

    [Test]
    public void DecideSwitchKnownNameUsesItsId()
    {
        var known = new List<Profile> { new() { Uuid = Id, Name = "Banana" } };
        ProfileSwitchListener.DecideSwitch("Pear", "banana", known).Should().Be((Id, "banana"));
    }

    [Test]
    public void DecideSwitchUnknownNameResetsId()
    {
        ProfileSwitchListener.DecideSwitch("Pear", "Kiwi", new List<Profile>()).Should().Be((Guid.Empty, "Kiwi"));
    }

    [TestCase("Pear", "Pear")]
    [TestCase("Pear", null)]
    [TestCase("Pear", "")]
    public void DecideSwitchIgnoresSameOrMissingName(string current, string reported)
    {
        ProfileSwitchListener.DecideSwitch(current, reported, new List<Profile>()).Should().BeNull();
    }

    [Test]
    public void ChatNameAndIdAreStoredAndUpserted()
    {
        var state = new StateObject();
        ProfileSwitchListener.ApplyChat(state, new[] { "You are playing on profile: Banana", "Profile ID: " + IdText });
        state.ExtractedInfo.CurrentProfileId.Should().Be(Id);
        state.ExtractedInfo.CurrentProfileName.Should().Be("Banana");
        state.Profiles.Should().ContainSingle(p => p.Uuid == Id && p.Name == "Banana");
        ProfileSwitchListener.ApplyChat(state, new[] { "You are playing on profile: Banana", "Profile ID: " + IdText });
        state.Profiles.Should().HaveCount(1);
    }

    [Test]
    public async Task TabWithUnknownNameResetsToUnknownProfile()
    {
        var state = new StateObject();
        ProfileSwitchListener.ApplyChat(state, new[] { "You are playing on profile: Banana", "Profile ID: " + IdText });
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.Tab, Tab = new[] { "Profile: Kiwi" } }
        };

        await new ProfileSwitchListener().Process(args);

        state.ExtractedInfo.CurrentProfileId.Should().Be(Guid.Empty);
        state.ExtractedInfo.CurrentProfileName.Should().Be("Kiwi");
    }

    [Test]
    public async Task TabNameIsAdoptedWhenOnlyTheIdIsKnown()
    {
        // regression: an id line without a preceding name line, then the tab name must not reset the known id
        var state = new StateObject();
        ProfileSwitchListener.ApplyChat(state, new[] { "Profile ID: " + IdText });
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.Tab, Tab = new[] { "Profile: Kiwi" } }
        };

        await new ProfileSwitchListener().Process(args);

        state.ExtractedInfo.CurrentProfileId.Should().Be(Guid.Parse(IdText));
        state.ExtractedInfo.CurrentProfileName.Should().Be("Kiwi");
        state.Profiles.Should().ContainSingle(p => p.Name == "Kiwi" && p.Uuid == Guid.Parse(IdText));
    }

    [Test]
    public async Task TabWithKnownNameSwitchesBack()
    {
        var state = new StateObject();
        ProfileSwitchListener.ApplyChat(state, new[] { "You are playing on profile: Banana", "Profile ID: " + IdText });
        ProfileSwitchListener.ApplyChat(state, new[] { "You are playing on profile: Kiwi" });
        var args = new MockedUpdateArgs
        {
            currentState = state,
            msg = new UpdateMessage { Kind = UpdateMessage.UpdateKind.Tab, Tab = new[] { "Profile: Banana" } }
        };

        await new ProfileSwitchListener().Process(args);

        state.ExtractedInfo.CurrentProfileId.Should().Be(Id);
    }

    [Test]
    public void ExtractedInfoCopyKeepsCurrentProfile()
    {
        var copy = new ExtractedInfo(new ExtractedInfo { CurrentProfileId = Id, CurrentProfileName = "Banana" });
        copy.CurrentProfileId.Should().Be(Id);
        copy.CurrentProfileName.Should().Be("Banana");
    }
}
