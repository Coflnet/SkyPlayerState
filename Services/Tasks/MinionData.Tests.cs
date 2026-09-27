using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks;

public class MinionDataTests
{
    [Test]
    public void EveryMinionProductTag_IsInDetectionItems()
    {
        // "Reflects the minion command's numbers" guarantee: MinionCollectionTask must never miss a
        // tag the minion command itself already knows about.
        MinionData.Minions.Should().NotBeEmpty("the embedded minion_data.json must load");
        foreach (var minion in MinionData.Minions)
        foreach (var product in minion.Products ?? [])
        {
            MinionData.DetectionItems.Should().Contain(product.Tag,
                $"{minion.Name}'s product {product.Tag} must be tracked");
        }
    }

    [Test]
    public void InfernoMinionMainProduct_IsDetected()
    {
        // regression: minion_data.json lists only the Inferno Minion's rare drops, its main output
        // (Crude Gabagool) made real inferno collection periods fall out of Minion Collection
        MinionData.ProductTags.Should().NotContain("CRUDE_GABAGOOL",
            "once the minion command's data lists it, drop it from ProductionVerifiedOutputs");
        new TaskClassifier(new TaskRegistry())
            .Classify("Your Island", new() { ["CRUDE_GABAGOOL"] = 628, ["VERY_CRUDE_GABAGOOL"] = 544 }, 10)
            ?.TaskName.Should().Be("Minion Collection");
    }

    [Test]
    public void DetectionItems_IncludesCompactedForms()
    {
        MinionData.ProductTags.Should().Contain("SLIME_BALL");
        MinionData.DetectionItems.Should().Contain("ENCHANTED_SLIME_BALL");
        MinionData.DetectionItems.Should().Contain("ENCHANTED_SLIME_BLOCK");
    }

    /// <summary>
    /// Kept byte-identical to the minion command's own source of truth so MinionCollectionTask never
    /// silently drifts from what /cofl minion (or the equivalent) reports. Ignored when the sibling
    /// repo checkout isn't present (e.g. CI without the full collection checked out).
    /// </summary>
    [Test]
    public void EmbeddedMinionData_IsByteIdenticalToSkyBackendForFrontendCopy()
    {
        var siblingPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "SkyBackendForFrontend", "Services", "minion_data.json"));
        if (!File.Exists(siblingPath))
        {
            Assert.Ignore($"sibling repo copy not found at {siblingPath} - skip when SkyBackendForFrontend isn't checked out alongside this repo");
            return;
        }

        var embeddedPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "Services", "Tasks", "minion_data.json"));
        if (!File.Exists(embeddedPath))
        {
            Assert.Ignore($"local copy not found at {embeddedPath}");
            return;
        }

        var embeddedBytes = File.ReadAllBytes(embeddedPath);
        var siblingBytes = File.ReadAllBytes(siblingPath);
        embeddedBytes.Should().Equal(siblingBytes, "Services/Tasks/minion_data.json must be an exact copy of the minion command's source data");
    }

    /// <summary>
    /// Every mapped enchanted/compacted tag must be a real item - verified against
    /// NotEnoughUpdates-REPO/items/&lt;TAG&gt;.json (the same repo TaskCatalog's research verdicts were
    /// checked against). Ignored when that repo isn't checked out alongside this one.
    /// </summary>
    [Test]
    public void ProductionVerifiedCompactedForms_AreDetected()
    {
        // regression: these were dropped because the NEU repo lacks them, but production periods
        // report them (wheat/sunflower minions with a compactor) - they must classify as minion output
        new MinionCollectionTask().GetDetectionSignature().DetectionItems
            .Should().Contain(["ENCHANTED_WHEAT", "ENCHANTED_SUNFLOWER"]);
    }

    [Test]
    public void EveryMappedEnchantedTag_ExistsAsANotEnoughUpdatesItem()
    {
        var itemsDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "NotEnoughUpdates-REPO", "items"));
        if (!Directory.Exists(itemsDir))
        {
            Assert.Ignore($"NotEnoughUpdates-REPO/items not found at {itemsDir} - skip when that repo isn't checked out alongside this one");
            return;
        }

        var missing = EnchantedForms.MapForTest.Values.SelectMany(v => v).Distinct()
            .Where(tag => !EnchantedForms.ProductionVerified.Contains(tag))
            .Where(tag => !File.Exists(Path.Combine(itemsDir, tag + ".json")))
            .ToList();

        missing.Should().BeEmpty("every enchanted form mapped in EnchantedForms must be a real, verified item id");
    }
}
