using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Tests;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class TabListUpdateTests
{
    [Test]
    public void ExtractIsland_ReadsAreaLine()
    {
        var tab = new[] { "§b§lSKYBLOCK", "§7Area: §fCrystal Hollows", "§7Purse: 5" };
        TabListUpdate.ExtractIsland(tab).Should().Be("Crystal Hollows");
    }

    [Test]
    public void ExtractIsland_NullWhenAbsent()
    {
        TabListUpdate.ExtractIsland(new[] { "§b§lSKYBLOCK", "§7Purse: 5" }).Should().BeNull();
    }

    [Test]
    public void ExtractIsland_IgnoresNullLines()
    {
        TabListUpdate.ExtractIsland(new[] { null, "Area: Hub" }).Should().Be("Hub");
    }

    [Test]
    public async Task Process_SetsCurrentIsland_AndKeepsLastTab()
    {
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.Tab,
                Tab = new[] { "Area: The Park", "some other line" }
            }
        };

        await new TabListUpdate().Process(args);

        args.currentState.ExtractedInfo.CurrentIsland.Should().Be("The Park");
        args.currentState.LastTab.Should().BeEquivalentTo(args.msg.Tab);
    }

    [Test]
    public async Task Process_LeavesCurrentIslandUnset_WhenTabHasNoAreaLine()
    {
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.Tab,
                Tab = new[] { "some other line" }
            }
        };

        await new TabListUpdate().Process(args);

        args.currentState.ExtractedInfo.CurrentIsland.Should().BeNull();
    }

    /// <summary>
    /// Regression (production logs, player Ekwav): Galatea's two islands' tab "Area:" lines report
    /// their entry sub-region ("Moonglade Marsh"/"Torrhus Canyon") instead of a clean island name
    /// like every other island - Process must normalize each to its OWN island via
    /// SkyblockZones.NormalizeTabArea. Corrected 2026-09 (game owner knowledge): Galatea is two
    /// separate islands with essentially disjoint content, not one - so "Torrhus Canyon" must
    /// resolve to "Torrhus", never to "Moonglade" or a shared "Galatea" island name.
    /// </summary>
    [Test]
    public async Task Process_NormalizesMoonglandeSubRegion_ToMoonglade()
    {
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.Tab,
                Tab = new[] { "Area: Moonglade Marsh" }
            }
        };

        await new TabListUpdate().Process(args);

        args.currentState.ExtractedInfo.CurrentIsland.Should().Be("Moonglade");
    }

    [Test]
    public async Task Process_NormalizesTorrhusSubRegion_ToTorrhus()
    {
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.Tab,
                Tab = new[] { "Area: Torrhus Canyon" }
            }
        };

        await new TabListUpdate().Process(args);

        args.currentState.ExtractedInfo.CurrentIsland.Should().Be("Torrhus");
    }

    [Test]
    public async Task Process_LeavesPrivateIslandUnchanged()
    {
        // "Private Island" is the literal tab value (distinct from the scoreboard's separately
        // ambiguous "Your Island") and is what task Locations use (e.g. MyceliumTask) - it must
        // pass through NormalizeTabArea unchanged, not get rewritten to "Your Island" or dropped.
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.Tab,
                Tab = new[] { "Area: Private Island" }
            }
        };

        await new TabListUpdate().Process(args);

        args.currentState.ExtractedInfo.CurrentIsland.Should().Be("Private Island");
    }

    [Test]
    public async Task Process_StampsCurrentIslandAt_WhenAreaLinePresent()
    {
        var args = new MockedUpdateArgs
        {
            currentState = new StateObject(),
            msg = new UpdateMessage
            {
                Kind = UpdateMessage.UpdateKind.Tab,
                Tab = new[] { "Area: Hub" }
            }
        };

        var before = DateTime.UtcNow;
        await new TabListUpdate().Process(args);
        var after = DateTime.UtcNow;

        args.currentState.ExtractedInfo.CurrentIslandAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
