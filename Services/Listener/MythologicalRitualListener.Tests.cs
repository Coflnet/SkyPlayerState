using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using Moq;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class MythologicalRitualListenerTests
{
    [Test]
    public void ContainsMythologicalRitual_WithDirectMatch_ReturnsTrue()
    {
        var description = "Some text Mythological Ritual more text";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.True);
    }

    [Test]
    public void ContainsMythologicalRitual_WithColorCodes_ReturnsTrue()
    {
        // Color code in between: §2Mythological\n§2Ritual
        var description = "§7during Diana's §2Mythological\n§2Ritual§7.";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.True);
    }

    [Test]
    public void ContainsMythologicalRitual_WithNewlineInMiddle_ReturnsTrue()
    {
        var description = "during Diana's Mythological\nRitual.";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.True);
    }

    [Test]
    public void ContainsMythologicalRitual_WithColorCodesAndNewline_ReturnsTrue()
    {
        // From the sample data: "§7during Diana's §2Mythological\n§2Ritual§7."
        var description = "§6Ability: Mythos' Might \n§7Grants §a2x §7this armor's stats while in\n§7§aThe Hub §7during Diana's §2Mythological\n§2Ritual§7.";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.True);
    }

    [Test]
    public void ContainsMythologicalRitual_WithMythologicalOnly_ReturnsFalse()
    {
        var description = "Some Mythological text without the full phrase";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.False);
    }

    [Test]
    public void ContainsMythologicalRitual_WithoutMatch_ReturnsFalse()
    {
        var description = "§7Health: §c+218 §e(+40) §9(+8)\n§7Defense: §a+113";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.False);
    }

    [Test]
    public void ContainsMythologicalRitual_WithNullOrEmpty_ReturnsFalse()
    {
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(null!), Is.False);
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(""), Is.False);
    }

    [Test]
    public void NormalizeText_RemovesColorCodes()
    {
        var text = "§7Health: §c+100";
        var result = MythologicalRitualListener.NormalizeText(text);
        Assert.That(result, Is.EqualTo("Health: +100"));
    }

    [Test]
    public void NormalizeText_RemovesNewlines()
    {
        var text = "Line1\nLine2";
        var result = MythologicalRitualListener.NormalizeText(text);
        Assert.That(result, Is.EqualTo("Line1 Line2"));
    }

    [Test]
    public void NormalizeText_CombinedRemoval()
    {
        var text = "§2Mythological\n§2Ritual";
        var result = MythologicalRitualListener.NormalizeText(text);
        Assert.That(result, Is.EqualTo("Mythological Ritual"));
    }

    [Test]
    public void ContainsMythologicalRitual_MythosBoots_ReturnsTrue()
    {
        // Full description from sample data
        var description = "§7Health: §c+218 §e(+40) §9(+8)\n§7Defense: §a+113 §e(+20) §9(+8)\n§7Strength: §c+20 §9(+10)\n§7Crit Chance: §9+10% §9(+10%)\n§7Crit Damage: §9+10% §9(+10%)\n§7Bonus Attack Speed: §e+4% §9(+4%)\n§7Intelligence: §b+10 §9(+10)\n§7Speed: §f+1 §9(+1)\n §8[§8⚔§8]\n\n§9Growth VI\n§7Grants §a+90 §c❤ Health§7.\n§9Protection VI\n§7Grants §a+25 §a❈ Defense§7.\n\n§6Ability: Mythos' Might \n§7Grants §a2x §7this armor's stats while in\n§7§aThe Hub §7during Diana's §2Mythological\n§2Ritual§7.\n\n§8Tiered Bonus: Unearthed (0/8)\n§7Reduces damage taken from §2§2✿\n§2Mythological §7mobs by §a0%§7.\n\n§8Tiered Bonus: Familiarity (0/4)\n§7§7Grants §b+0✯ Magic Find §7on §2§2✿\n§2Mythological §7mobs.\n\n§9Renowned Bonus\n§7Increases all §cCombat §7stats and §b✯\n§bMagic Find §7by §a+1%§7.\n\n§6§lLEGENDARY BOOTS";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.True);
    }

    [Test]
    public void ContainsMythologicalRitual_ChallengerBracelet_ReturnsTrue()
    {
        // From sample data
        var description = "§7Health: §c+20\n§7Defense: §a+10\n§7Strength: §c+5\n\n§6Ability: Mythos' Might \n§7Grants §a2x §7this equipment's stats\n§7while in §aThe Hub §7during Diana's\n§7§2Mythological Ritual§7.";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.True);
    }

    [Test]
    public void ContainsMythologicalRitual_ArchaicSpade_ReturnsTrue()
    {
        // From sample data
        var description = "§7Use to reveal and dig up §eGriffin\n§eBurrows §7in the hub while Diana's\n§7§2Mythological Ritual §7is active.";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.True);
    }

    [Test]
    public void ContainsMythologicalRitual_DaedalusBlade_ReturnsFalse()
    {
        // This item mentions "Mythological" mobs but not "Mythological Ritual"
        var description = "§7Grants §c+1% ❁ Damage §7and §b+0.2✯\n§bMagic Find §7on §2§2✿ Mythological §7mobs\n§7per §3Bestiary §7tier you have across\n§7all of them.";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.False);
    }

    [Test]
    public void ContainsMythologicalRitual_WheatMinion_ReturnsFalse()
    {
        var description = "§7Place this minion and it will start\n§7generating and harvesting wheat.\n§7Requires dirt or soil nearby so\n§7wheat can be planted. Minions also\n§7work when you are offline!";
        Assert.That(MythologicalRitualListener.ContainsMythologicalRitual(description), Is.False);
    }
}
