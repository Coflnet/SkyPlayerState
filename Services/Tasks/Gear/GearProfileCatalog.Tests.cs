using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

/// <summary>
/// The first block validates every embedded profiles/*.json, so a profile added later is checked without new
/// tests. The rest proves each rule fires, using the synthetic fixtures (GearFixtures.Tests.cs).
/// </summary>
public class GearProfileCatalogTests
{
    private static readonly TaskRegistry Registry = new();

    // ── every embedded profile ──

    [Test]
    public void EmbeddedProfiles_AllLoadAndPassValidation()
    {
        var catalog = GearProfileCatalog.Embedded;

        catalog.Errors.Should().BeEmpty("every profiles/*.json must parse and pass GearProfileValidator");
        catalog.Profiles.Should().HaveCount(catalog.SourceNames.Count, "no embedded file may be skipped");
    }

    [Test]
    public void EmbeddedProfiles_AreEmbeddedUnderTheCatalogPrefix()
    {
        var resources = typeof(GearProfileCatalog).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(GearProfileCatalog.ResourcePrefix, StringComparison.Ordinal)).ToList();

        GearProfileCatalog.Embedded.SourceNames.Should().BeEquivalentTo(resources);
    }

    [Test]
    public void EmbeddedProfiles_BindAndUnlockOnlyRegisteredPublicTasks()
    {
        GearProfileValidator.RegistryErrors(GearProfileCatalog.Embedded, Registry).Should().BeEmpty();
    }

    [Test]
    public void EmbeddedProfiles_ItemTagsExistInNotEnoughUpdatesRepo()
    {
        var itemsDir = NeuItemsDir();
        if (!Directory.Exists(itemsDir))
        {
            if (Environment.GetEnvironmentVariable("REQUIRE_NEU_REPO") == "1")
                Assert.Fail($"REQUIRE_NEU_REPO=1 but NotEnoughUpdates-REPO/items is missing at {itemsDir}");
            Assert.Ignore($"NotEnoughUpdates-REPO/items not found at {itemsDir} - skip when that repo isn't checked out alongside this one");
            return;
        }

        var missing = GearProfileCatalog.Embedded.Profiles.SelectMany(AllTags).Distinct()
            .Where(tag => !NeuItemExists(itemsDir, tag)).ToList();

        missing.Should().BeEmpty("every gear profile item and rare drop tag must be a real NEU item id");
    }

    /// <summary>
    /// Regression: about 40 items carried made-up display names (e.g. MANGROVE_VINE as "Mangrove Vine", it is the
    /// Moonglade Belt). Names must match NEU's displayname, see <see cref="NeuNameMatches"/> for the tolerated forms.
    /// </summary>
    [Test]
    public void EmbeddedProfiles_ItemNamesMatchNotEnoughUpdates()
    {
        var itemsDir = NeuItemsDir();
        if (!Directory.Exists(itemsDir))
        {
            if (Environment.GetEnvironmentVariable("REQUIRE_NEU_REPO") == "1")
                Assert.Fail($"REQUIRE_NEU_REPO=1 but NotEnoughUpdates-REPO/items is missing at {itemsDir}");
            Assert.Ignore($"NotEnoughUpdates-REPO/items not found at {itemsDir}");
            return;
        }

        var wrong = GearProfileCatalog.Embedded.Profiles.SelectMany(AllItems)
            .Where(i => NeuItemExists(itemsDir, i.ItemTag) && !NeuNameMatches(i.ItemTag, i.Name, NeuDisplayName(itemsDir, i.ItemTag)))
            .Select(i => $"{i.ItemTag}: '{i.Name}' vs NEU '{NeuDisplayName(itemsDir, i.ItemTag)}'")
            .Distinct().ToList();

        wrong.Should().BeEmpty("every item name must be the item's real display name");
    }

    [TestCase("MANGROVE_VINE", "Moonglade Belt", "§9Moonglade Belt", true)]
    [TestCase("MANGROVE_VINE", "Mangrove Vine", "§9Moonglade Belt", false)]
    [TestCase("THEORETICAL_HOE_WHEAT_1", "Euclid's Wheat Sickle Mk I", "§aEuclid's Wheat Sickle Mk. I", true)]
    [TestCase("FINE_RUBY_GEM", "Fine Ruby Gemstone", "§a❤ Fine Ruby Gemstone", true)]
    [TestCase("PET_ELEPHANT", "Elephant (Legendary, level 100)", "§f[Lvl {LVL}] Elephant", true)]
    [TestCase("PET_ARMADILLO", "Armadillo Pet", "§f[Lvl {LVL}] Armadillo", true)]
    [TestCase("PET_ARMADILLO", "Elephant Pet", "§f[Lvl {LVL}] Armadillo", false)]
    [TestCase("PET_CARPET", "Carpet Pet", "§f[Lvl {LVL}] Carpet", true)]     // only a trailing " pet" is dropped
    [TestCase("PET_CARPET", "Car", "§f[Lvl {LVL}] Carpet", false)]
    [TestCase("IMPLOSION_SCROLL", "Implosion Scroll", "§5Implosion", true)]
    [TestCase("DIVAN_HELMET", "Divan Helmet", "§6Helmet of Divan", false)]
    public void NeuNameMatches_ToleratesOnlyFormatting(string tag, string name, string neu, bool expected)
    {
        NeuNameMatches(tag, name, neu).Should().Be(expected);
    }

    private static string NeuDisplayName(string itemsDir, string tag)
    {
        var path = Path.Combine(itemsDir, tag + ".json");
        if (!File.Exists(path))
            path = Directory.EnumerateFiles(itemsDir, tag["PET_".Length..] + ";*.json").First();
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("displayname").GetString() ?? "";
    }

    /// <summary>
    /// Equal after dropping colour codes, the pet "[Lvl {LVL}]" prefix and gem glyphs, ignoring case and the "Mk."
    /// dot. Pets may add " Pet" or a "(Rarity, level N)" note, Hyperion scrolls the "Scroll" suffix.
    /// </summary>
    internal static bool NeuNameMatches(string tag, string name, string neuDisplayName)
    {
        var profile = NormalizeName(name);
        if (tag.StartsWith("PET_", StringComparison.Ordinal))
            profile = Regex.Replace(Regex.Replace(profile, @"\s*\(.*\)$", ""), @" pet$", "").Trim();
        if (tag.EndsWith("_SCROLL", StringComparison.Ordinal) && profile.EndsWith(" scroll", StringComparison.Ordinal))
            profile = profile[..^" scroll".Length];
        return profile == NormalizeName(neuDisplayName);
    }

    private static string NormalizeName(string name)
    {
        var plain = Regex.Replace(name, "§.", "").Replace("[Lvl {LVL}]", "");
        plain = Regex.Replace(plain, @"[^A-Za-z0-9' .()\-]", "");
        plain = Regex.Replace(plain, @"\bMk\.", "Mk");
        return Regex.Replace(plain, @"\s+", " ").Trim().ToLowerInvariant();
    }

    private static IEnumerable<GearItem> AllItems(GearProfile profile) =>
        profile.AllSetups().SelectMany(s => s.Items)
            .Concat(profile.Bindings.SelectMany(b => b.ItemOverrides?.Values ?? Enumerable.Empty<GearItem>()));

    [Test]
    public void NeuLookup_MapsPetTagsToTheirRarityFiles()
    {
        var itemsDir = NeuItemsDir();
        if (!Directory.Exists(itemsDir))
        {
            Assert.Ignore($"NotEnoughUpdates-REPO/items not found at {itemsDir}");
            return;
        }

        NeuItemExists(itemsDir, "ENCHANTED_COAL").Should().BeTrue();
        NeuItemExists(itemsDir, "PET_BEE").Should().BeTrue("pets are stored as BEE;<rarity>.json");
        NeuItemExists(itemsDir, "SYNTHETIC_HELMET_BUDGET").Should().BeFalse();
    }

    /// <summary>NEU_REPO_DIR overrides the checkout, for a local clone that is behind upstream/master.</summary>
    private static string NeuItemsDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("NEU_REPO_DIR");
        if (!string.IsNullOrEmpty(overrideDir))
            return Path.Combine(overrideDir, "items");
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "NotEnoughUpdates-REPO", "items"));
    }

    /// <summary>NEU stores plain items as TAG.json and pets (PET_X) as X;&lt;rarity&gt;.json.</summary>
    private static bool NeuItemExists(string itemsDir, string tag)
    {
        if (File.Exists(Path.Combine(itemsDir, tag + ".json")))
            return true;
        return tag.StartsWith("PET_", StringComparison.Ordinal)
               && Directory.EnumerateFiles(itemsDir, tag["PET_".Length..] + ";*.json").Any();
    }

    private static IEnumerable<string> AllTags(GearProfile profile)
    {
        var setupTags = profile.AllSetups().SelectMany(s => s.Items).Select(i => i.ItemTag);
        var overrideTags = profile.Bindings.SelectMany(b => b.ItemOverrides?.Values ?? Enumerable.Empty<GearItem>()).Select(i => i.ItemTag);
        var rareTags = profile.Bindings.SelectMany(b => b.RareDropTags ?? []);
        return setupTags.Concat(overrideTags).Concat(rareTags);
    }

    // ── fixtures: valid shapes ──

    [Test]
    public void Fixtures_CoverEveryModelKindAndAreValid()
    {
        var catalog = GearFixtures.All();

        catalog.Errors.Should().BeEmpty();
        catalog.Profiles.Select(p => GearModelKinds.Normalize(p.Model.Kind)).Should().BeEquivalentTo(GearModelKinds.All);
        GearProfileValidator.RegistryErrors(catalog, Registry).Should().BeEmpty();
    }

    [Test]
    public void TryGet_FindsTheBindingCaseInsensitively()
    {
        GearFixtures.All().TryGet("wheat farming", out var bound).Should().BeTrue();

        bound.Profile.Id.Should().Be("test-fortune");
        bound.Binding.RareDropTags.Should().Equal("SYNTHETIC_RARE_DROP");
    }

    [Test]
    public void Resolve_AppliesSlotAndScopedItemOverridesAndStatOverrides()
    {
        GearFixtures.All().TryGet("Carrot Farming", out var bound).Should().BeTrue();

        var resolved = GearBindingResolver.Resolve(bound.Profile, bound.Binding);

        resolved.Baseline.Items.Single(i => i.Slot == "tool").ItemTag.Should().Be("SYNTHETIC_CARROT_TOOL");
        resolved.Setups.Single(s => s.Id == "mid").Items.Single(i => i.Slot == "tool").ItemTag.Should().Be("SYNTHETIC_CARROT_TOOL");
        resolved.Setups.Single(s => s.Id == "endgame").Items.Single(i => i.Slot == "tool").ItemTag.Should().Be("SYNTHETIC_CARROT_TOOL_ENDGAME");
        resolved.Setups.Single(s => s.Id == "endgame").Stats["Farming Fortune"].Should().Be(250);
        resolved.Setups.Single(s => s.Id == "mid").Stats["Farming Fortune"].Should().Be(150);
        bound.Profile.Setups.Single(s => s.Id == "endgame").Stats["Farming Fortune"].Should().Be(200, "the profile itself is not mutated");
    }

    [Test]
    public void Resolve_AppliesModelOverrides()
    {
        GearFixtures.All().TryGet("Coal Mining", out var bound).Should().BeTrue();

        GearBindingResolver.Resolve(bound.Profile, bound.Binding).Model.GetDouble("blockStrength").Should().Be(600);
    }

    [Test]
    public void EmptyCatalog_HasNoBindings()
    {
        var catalog = GearFixtures.Catalog();

        catalog.Profiles.Should().BeEmpty();
        catalog.TryGet("Wheat Farming", out _).Should().BeFalse();
    }

    // ── fixtures: each rule fires ──

    private static IReadOnlyList<string> ErrorsOf(JsonObject profile) => GearFixtures.Catalog(profile.ToJsonString()).Errors;

    private static JsonObject Setup(JsonObject profile, string id) =>
        id == "baseline" ? profile["baseline"]!.AsObject()
            : profile["setups"]!.AsArray().Select(s => s!.AsObject()).Single(s => (string?)s["id"] == id);

    [Test]
    public void Invalid_MissingTier()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        profile["setups"]!.AsArray().RemoveAt(2);

        ErrorsOf(profile).Should().Contain(e => e.Contains("exactly one each of budget, mid, endgame"));
    }

    [Test]
    public void Invalid_DuplicateTier()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        Setup(profile, "mid")["tier"] = "budget";

        ErrorsOf(profile).Should().Contain(e => e.Contains("exactly one each"));
    }

    [Test]
    public void Invalid_BaselineWithAnotherId()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        Setup(profile, "baseline")["id"] = "start";

        ErrorsOf(profile).Should().Contain(e => e.Contains("baseline must have id 'baseline'"));
    }

    [Test]
    public void Invalid_DuplicateSetupId()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        Setup(profile, "mid")["id"] = "budget";

        ErrorsOf(profile).Should().Contain(e => e.Contains("setup id 'budget' is used more than once"));
    }

    [Test]
    public void Invalid_DuplicateSlot()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        Setup(profile, "mid")["items"]!.AsArray()[1]!["slot"] = "helmet";

        ErrorsOf(profile).Should().Contain(e => e.Contains("uses slot 'helmet' more than once"));
    }

    [Test]
    public void Invalid_ItemWithoutReasonOrBadTag()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        var item = Setup(profile, "mid")["items"]!.AsArray()[0]!;
        item["reason"] = "";
        item["tag"] = "not a tag";

        var errors = ErrorsOf(profile);

        errors.Should().Contain(e => e.Contains("needs a reason"));
        errors.Should().Contain(e => e.Contains("is not an item tag"));
    }

    [TestCase(null)]
    [TestCase("http://example.com/insecure")]
    [TestCase("not a url")]
    public void Invalid_Sources(string? source)
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        profile["sources"] = source == null ? new JsonArray() : new JsonArray(source);

        ErrorsOf(profile).Should().Contain(e => e.Contains("sources"));
    }

    [Test]
    public void Invalid_UnknownAnchor()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        profile["anchor"] = "cheap";

        ErrorsOf(profile).Should().Contain(e => e.Contains("anchor 'cheap'"));
    }

    [Test]
    public void Invalid_UnknownModelKind()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        profile["model"]!["kind"] = "magic";

        ErrorsOf(profile).Should().Contain(e => e.Contains("model.kind"));
    }

    [TestCase(GearModelKinds.Fortune, "fortuneStat")]
    [TestCase(GearModelKinds.Mining, "speedStat")]
    [TestCase(GearModelKinds.Mining, "fortuneStats")]
    [TestCase(GearModelKinds.Mining, "blockStrength")]
    [TestCase(GearModelKinds.Fishing, "fishingSpeedStat")]
    [TestCase(GearModelKinds.Fishing, "seaCreatureChanceStat")]
    [TestCase(GearModelKinds.Fishing, "baseWaitSeconds")]
    [TestCase(GearModelKinds.RunTime, "rareStat")]
    [TestCase(GearModelKinds.Fixed, "multipliers")]
    public void Invalid_MissingRequiredModelParam(string kind, string param)
    {
        var profile = GearFixtures.Mutable(GearFixtures.ByKind[kind]);
        profile["model"]!.AsObject().Remove(param).Should().BeTrue("the fixture sets it");
        profile["bindings"]![0]!.AsObject().Remove("modelOverrides"); // the mining binding re-supplies blockStrength

        ErrorsOf(profile).Should().Contain(e => e.Contains($"model.{param}"));
    }

    [Test]
    public void Invalid_ModelStatMissingInASetup()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Mining);
        Setup(profile, "mid")["stats"]!.AsObject().Remove("Mining Spread");

        ErrorsOf(profile).Should().Contain(e => e.Contains("setup 'mid' must list stat 'Mining Spread'"));
    }

    [Test]
    public void Invalid_AnchorWithZeroFactors()
    {
        // chest value is proportional to the chance stat: an anchor without any would divide by zero
        var profile = GearFixtures.Mutable(GearFixtures.Mining);
        profile["model"]!["chanceStat"] = "Mining Spread"; // the budget anchor has 0 spread

        ErrorsOf(profile).Should().Contain(e => e.Contains("anchor setup 'budget' must give positive rate factors"));
    }

    [TestCase("certain")]
    [TestCase("2")]
    public void Invalid_ModelConfidence(string value)
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fixed);
        profile["modelConfidence"] = value;

        ErrorsOf(profile).Should().Contain(e => e.Contains("modelConfidence must be high, medium or low"));
    }

    [Test]
    public void Invalid_NegativeStatCap()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fixed);
        profile["statCaps"] = new JsonObject { ["Magic Find"] = -1 };

        ErrorsOf(profile).Should().Contain(e => e.Contains("statCaps"));
    }

    [Test]
    public void Invalid_RunTimeSetupWithoutRunSeconds()
    {
        var profile = GearFixtures.Mutable(GearFixtures.RunTime);
        Setup(profile, "endgame").Remove("runSeconds");

        ErrorsOf(profile).Should().Contain(e => e.Contains("setup 'endgame' needs runSeconds"));
    }

    [Test]
    public void Invalid_FixedWithoutMultiplierForASetup()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fixed);
        profile["model"]!["multipliers"]!.AsObject().Remove("mid");

        ErrorsOf(profile).Should().Contain(e => e.Contains("multipliers needs a number > 0 for setup 'mid'"));
    }

    [Test]
    public void Invalid_TierUnlockSetupWithoutUnlock()
    {
        var profile = GearFixtures.Mutable(GearFixtures.TierUnlock);
        Setup(profile, "endgame").Remove("unlocksTask");

        ErrorsOf(profile).Should().Contain(e => e.Contains("setup 'endgame' needs unlocksTask"));
    }

    [Test]
    public void Invalid_TierUnlockWithoutTiers()
    {
        var profile = GearFixtures.Mutable(GearFixtures.TierUnlock);
        profile.Remove("tiers");

        ErrorsOf(profile).Should().Contain(e => e.Contains("tiers (at least two task names, lowest tier first) is required"));
    }

    [Test]
    public void Invalid_TierLadderMissingABoundOrUnlockedTask()
    {
        var profile = GearFixtures.Mutable(GearFixtures.TierUnlock);
        profile["tiers"] = new JsonArray("Kuudra T1", "Kuudra T2", "Kuudra T3", "Kuudra T4");

        var errors = ErrorsOf(profile);

        errors.Should().Contain(e => e.Contains("binding task 'Kuudra T5' is not in tiers"));
        errors.Should().Contain(e => e.Contains("setup 'endgame' unlocksTask 'Kuudra T5' is not in tiers"));
    }

    [Test]
    public void Invalid_UpgradesMustClimbTheLadder()
    {
        var profile = GearFixtures.Mutable(GearFixtures.TierUnlock);
        Setup(profile, "mid")["unlocksTask"] = "Kuudra T3"; // same tier as budget

        ErrorsOf(profile).Should().Contain(e => e.Contains("setup 'mid' must unlock a higher tier"));
    }

    [Test]
    public void Invalid_BaselineUnlockOrRunSecondsOnTierUnlock()
    {
        var profile = GearFixtures.Mutable(GearFixtures.TierUnlock);
        Setup(profile, "baseline")["unlocksTask"] = "Kuudra T2";
        Setup(profile, "mid")["runSeconds"] = 300;

        var errors = ErrorsOf(profile);

        errors.Should().Contain(e => e.Contains("the baseline must not have unlocksTask"));
        errors.Should().Contain(e => e.Contains("runSeconds is not used by kind tierUnlock"));
    }

    [Test]
    public void Invalid_TiersOnAScaledModel()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        profile["tiers"] = new JsonArray("Wheat Farming", "Carrot Farming");

        ErrorsOf(profile).Should().Contain(e => e.Contains("tiers is only used by model kind tierUnlock"));
    }

    [TestCase("roughValueShare", 0.1)]   // removed parameter left in a file
    [TestCase("blockHardnes", 50.0)]     // typo
    public void Invalid_UnknownModelParam(string param, double value)
    {
        var profile = GearFixtures.Mutable(GearFixtures.Mining);
        profile["model"]![param] = value;

        ErrorsOf(profile).Should().Contain(e => e.Contains($"model.{param} is not a parameter of kind mining"));
    }

    [Test]
    public void EmptyFortuneStats_MeansNoFortune()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Mining);
        profile["model"]!["fortuneStats"] = new JsonArray();

        ErrorsOf(profile).Should().BeEmpty();
    }

    [Test]
    public void Invalid_RareChanceStatZeroAtTheAnchor()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Mining);
        profile["model"]!["rareChanceStat"] = "Mining Spread"; // the budget anchor has 0 spread

        ErrorsOf(profile).Should().Contain(e => e.Contains("anchor setup 'budget' must give positive rate factors"));
    }

    [Test]
    public void Invalid_DuplicateFortuneStats()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Mining);
        profile["model"]!["fortuneStats"] = new JsonArray("Mining Fortune", "mining fortune");

        ErrorsOf(profile).Should().Contain(e => e.Contains("fortuneStats lists 'Mining Fortune' more than once"));
    }

    [TestCase(4.9)]
    [TestCase(20.5)]
    public void Invalid_MaxMultiplierOutOfRange(double value)
    {
        var profile = GearFixtures.Mutable(GearFixtures.Mining);
        profile["maxMultiplier"] = value;

        ErrorsOf(profile).Should().Contain(e => e.Contains("maxMultiplier must be between 5 and 20"));
    }

    [Test]
    public void Invalid_MaxMultiplierOnTierUnlock()
    {
        var profile = GearFixtures.Mutable(GearFixtures.TierUnlock);
        profile["maxMultiplier"] = 10;

        ErrorsOf(profile).Should().Contain(e => e.Contains("maxMultiplier is not used by kind tierUnlock"));
    }

    [Test]
    public void Invalid_MinTicksBelowOne()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Mining);
        profile["model"]!["minTicks"] = 0;

        ErrorsOf(profile).Should().Contain(e => e.Contains("model.minTicks must be a number >= 1"));
    }

    /// <summary>
    /// Regression: stat keys differing only in case made the case-insensitive merge throw inside the static Lazy
    /// catalog, so every gear endpoint returned 500 until a restart.
    /// </summary>
    [TestCase("stats")]
    [TestCase("model")]
    [TestCase("statOverrides")]
    public void Invalid_KeysDifferingOnlyInCase_AreReportedNotThrown(string where)
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        switch (where)
        {
            case "stats": Setup(profile, "mid")["stats"]!["farming fortune"] = 1; break;
            case "model": profile["model"]!["FortuneStat"] = "Farming Fortune"; break;
            default:
                profile["bindings"]![1]!["statOverrides"]!["endgame"]!["FARMING FORTUNE"] = 1; break;
        }

        IReadOnlyList<string> errors = [];
        var load = () => errors = ErrorsOf(profile);

        load.Should().NotThrow();
        errors.Should().Contain(e => e.Contains("keys that differ only in case"));
    }

    [Test]
    public void Invalid_UnlocksTaskOutsideTierUnlock()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        Setup(profile, "endgame")["unlocksTask"] = "Kuudra T5";

        ErrorsOf(profile).Should().Contain(e => e.Contains("unlocksTask is only allowed for model kind tierUnlock"));
    }

    [Test]
    public void Invalid_ModelOverrideBreaksTheModel()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Mining);
        profile["bindings"]![0]!["modelOverrides"]!["blockStrength"] = -1;

        ErrorsOf(profile).Should().Contain(e => e.Contains("binding 'Coal Mining'") && e.Contains("model.blockStrength"));
    }

    [Test]
    public void Invalid_OverrideKeysNamingUnknownSetups()
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fortune);
        var carrot = profile["bindings"]![1]!.AsObject();
        carrot["statOverrides"] = new JsonObject { ["legendary"] = new JsonObject { ["Farming Fortune"] = 1 } };
        carrot["itemOverrides"]!.AsObject().Add("legendary:tool", JsonNode.Parse("""{ "tag": "SYNTHETIC_X", "name": "x", "reason": "x" }"""));

        var errors = ErrorsOf(profile);

        errors.Should().Contain(e => e.Contains("statOverrides key 'legendary'"));
        errors.Should().Contain(e => e.Contains("itemOverrides key 'legendary:tool'"));
    }

    [Test]
    public void Invalid_NoBindingsOrDuplicateBinding()
    {
        var none = GearFixtures.Mutable(GearFixtures.Fixed);
        none["bindings"] = new JsonArray();
        var duplicate = GearFixtures.Mutable(GearFixtures.Fixed);
        duplicate["bindings"]!.AsArray().Add(JsonNode.Parse("""{ "task": "diana" }"""));

        ErrorsOf(none).Should().Contain(e => e.Contains("bindings must bind at least one task"));
        ErrorsOf(duplicate).Should().Contain(e => e.Contains("is bound more than once"));
    }

    [Test]
    public void Invalid_ProfilesSharingAnIdOrATask()
    {
        var sameTask = GearFixtures.Mutable(GearFixtures.Fixed);
        sameTask["id"] = "test-fixed-2";

        var catalog = GearFixtures.Catalog(GearFixtures.Fixed, GearFixtures.Fixed, sameTask.ToJsonString());

        catalog.Errors.Should().Contain(e => e.Contains("id is used by another file"));
        catalog.Errors.Should().Contain(e => e.Contains("task 'Diana' is bound by profiles 'test-fixed' and 'test-fixed-2'"));
        catalog.Profiles.Should().HaveCount(2);
    }

    [Test]
    public void Invalid_JsonIsReportedNotThrown()
    {
        GearFixtures.Catalog("{ not json").Errors.Should().ContainSingle(e => e.Contains("invalid json"));
    }

    [TestCase("Not A Task", "binding task 'Not A Task'")]
    [TestCase("Bazaar Purchases", "binding task 'Bazaar Purchases'")] // hidden accounting task
    public void RegistryErrors_BindingToUnknownOrHiddenTask(string task, string expected)
    {
        var profile = GearFixtures.Mutable(GearFixtures.Fixed);
        profile["bindings"]![0]!["task"] = task;

        var catalog = GearFixtures.Catalog(profile.ToJsonString());

        catalog.Errors.Should().BeEmpty("task existence is a registry check, not a structural one");
        GearProfileValidator.RegistryErrors(catalog, Registry).Should().ContainSingle(e => e.Contains(expected));
    }

    [Test]
    public void RegistryErrors_UnlockingAnUnknownTask()
    {
        var profile = GearFixtures.Mutable(GearFixtures.TierUnlock);
        profile["tiers"]!.AsArray().Add("Kuudra T9");
        Setup(profile, "endgame")["unlocksTask"] = "Kuudra T9";
        var catalog = GearFixtures.Catalog(profile.ToJsonString());

        catalog.Errors.Should().BeEmpty();
        var errors = GearProfileValidator.RegistryErrors(catalog, Registry);
        errors.Should().Contain(e => e.Contains("unlocksTask 'Kuudra T9'"));
        errors.Should().Contain(e => e.Contains("tier 'Kuudra T9'"));
    }

    // ── shipped profile content (gear review 2026-11-12) ──

    private static GearProfile Shipped(string task) =>
        GearProfileCatalog.Embedded.TryGet(task, out var bound) ? GearBindingResolver.Resolve(bound.Profile, bound.Binding).Profile with
        {
            Baseline = GearBindingResolver.Resolve(bound.Profile, bound.Binding).Baseline,
            Setups = GearBindingResolver.Resolve(bound.Profile, bound.Binding).Setups.ToList()
        } : throw new AssertionException($"{task} has no gear profile");

    private static GearSetup SetupOf(GearProfile profile, string id) => profile.AllSetups().Single(s => s.Id == id);

    /// <summary>Regression: tasks whose base activity has a profile had no setups at all.</summary>
    [TestCase("Crimson Fishing (Hunting)")]
    [TestCase("Bayou Fishing (Hunting)")]
    [TestCase("Galatea Fishing (Hunting)")]
    [TestCase("Water Fishing (Hunting)")]
    [TestCase("Glacite Mineshafts")]
    [TestCase("Scatha Mining")]
    [TestCase("Sludge Mining")]
    [TestCase("Sludge Mining (Gem Mixture)")]
    [TestCase("Jungle Powder Mining")]
    [TestCase("Goblin Holdout Powder Mining")]
    [TestCase("Precursor City Powder Mining")]
    [TestCase("Mithril Deposits Powder Mining")]
    [TestCase("Crystal Hollows Powder Mining")]
    public void ActivityVariants_HaveAGearProfile(string task)
        => GearProfileCatalog.Embedded.TryGet(task, out _).Should().BeTrue();

    /// <summary>Regression: Mining Spread was 300 in every tier and the Mineral armor the measured setups pick was never offered.</summary>
    [Test]
    public void OreMining_OffersMineralArmorAndGrowingSpread()
    {
        var coal = Shipped("Coal Mining");

        SetupOf(coal, "budget").Items.Single(i => i.Slot == "helmet").ItemTag.Should().Be("MINERAL_HELMET");
        SetupOf(coal, "mid").Items.Single(i => i.Slot == "helmet").ItemTag.Should().Be("GLOSSY_MINERAL_HELMET");
        var spread = coal.AllSetups().Select(s => s.Stats["Mining Spread"]).ToList();
        spread.Should().BeInAscendingOrder().And.HaveCount(4);
        spread.Distinct().Count().Should().BeGreaterThan(2);
    }

    [Test]
    public void DwarvenMetalMining_KeepsTheMiningSpeedArmorAndFlatSpread()
    {
        var mithril = Shipped("Mithril Mining");

        SetupOf(mithril, "budget").Items.Single(i => i.Slot == "helmet").ItemTag.Should().Be("ARMOR_OF_YOG_HELMET");
        SetupOf(mithril, "endgame").Items.Single(i => i.Slot == "helmet").ItemTag.Should().Be("DIVAN_HELMET");
        mithril.AllSetups().Select(s => s.Stats["Mining Spread"]).Distinct().Should().ContainSingle();
    }

    /// <summary>Regression: the Jasper drill was labelled a "Starter gemstone drill" for baseline, budget and mid.</summary>
    [Test]
    public void GemMining_NoSetupIsCalledAStarterDrill()
    {
        var jasper = Shipped("Jasper Mining");

        jasper.AllSetups().Select(s => s.Name).Should().NotContain(n => n.Contains("drill", StringComparison.OrdinalIgnoreCase) && n.Contains("Starter"));
        SetupOf(jasper, "baseline").Items.Single(i => i.Slot == "tool").Reason.Should().Contain("every tier");
    }

    /// <summary>Regression: Kuudra setups had no weapon below endgame while the task steps ask for a Hyperion.</summary>
    [Test]
    public void Kuudra_EverySetupHasAWeapon()
    {
        var t1 = Shipped("Kuudra T1");

        t1.AllSetups().Should().OnlyContain(s => s.Items.Any(i => i.Slot == "weapon"));
    }
}
