#nullable enable
using System.Collections.Generic;
using System.Text;
using Coflnet.Sky.PlayerState.Models;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;
public class ItemCompareTests
{
    [Test]
    public void CompareNested()
    {
        var comparer = new ItemCompare();
        var a = new Item() { ExtraAttributes = new() { { "a", "b" }, { "nest", new Dictionary<string, object>() { { "RUNE", 1 } } } } };
        var b = new Item() { ExtraAttributes = new() { { "a", "b" }, { "nest", new Dictionary<string, object>() { { "RUNE", (byte)1 } } } } };
        Assert.That(comparer.GetHashCode(a), Is.EqualTo(comparer.GetHashCode(b)));
        Assert.That(comparer.Equals(a, b));
    }
    [Test]
    public void CompareArray()
    {
        var comparer = new ItemCompare();
        var a = new Item() { ExtraAttributes = new() { { "array", new string[] { "a", "b" } } } };
        var b = new Item() { ExtraAttributes = new() { { "array", new string[] { "a", "b" } } } };
        Assert.That(comparer.GetHashCode(a), Is.EqualTo(comparer.GetHashCode(b)));
        Assert.That(comparer.Equals(a, b));
    }
    [Test]
    public void CompareEnchants()
    {
        var comparer = new ItemCompare();
        var a = new Item() { ExtraAttributes = new() { { "array", "b" } }, Enchantments = new() { { "a", 1 } } };
        var b = new Item() { ExtraAttributes = new() { { "array", "b" } }, Enchantments = new() { { "a", 1 } } };
        Assert.That(comparer.GetHashCode(a), Is.EqualTo(comparer.GetHashCode(b)));
        Assert.That(comparer.Equals(a, b));
    }
    [Test]
    public void TopLevelNumberTypes()
    {
        var comparer = new ItemCompare();
        var a = new Item() { ExtraAttributes = new() { { "num", 200d } } };
        var b = new Item() { ExtraAttributes = new() { { "num", (ushort)200 } } };
        Assert.That(comparer.GetHashCode(a), Is.EqualTo(comparer.GetHashCode(b)));
        Assert.That(comparer.Equals(a, b));
    }
    [Test]
    public void UpgradeEnchantment()
    {
        var comparer = new ItemCompare();
        var a = new Item() { ExtraAttributes = new(), Enchantments = new() { { "sharpness", 1 } } };
        var b = new Item() { ExtraAttributes = new(), Enchantments = new() { { "sharpness", 2 } } };
        Assert.That(comparer.GetHashCode(a), Is.EqualTo(comparer.GetHashCode(b)));
        Assert.That(!comparer.Equals(a, b));
    }
    [Test]
    public void NoEnchants()
    {
        var comparer = new ItemCompare();
        var a = new Item() { ExtraAttributes = new() { { "array", "b" } } };
        var b = new Item() { ExtraAttributes = new() { { "array", "b" } } };
        Assert.That(comparer.GetHashCode(a), Is.EqualTo(comparer.GetHashCode(b)));
        Assert.That(comparer.Equals(a, b));
    }
    [Test]
    public void CompareComplex()
    {
        var comparer = new ItemCompare();
        var a = NewMethod();
        var b = NewMethod();
        b.Tag = new StringBuilder(b.Tag).Replace("R", "R").ToString();
        Assert.That(comparer.GetHashCode(a), Is.EqualTo(comparer.GetHashCode(b)));
        Assert.That(comparer.Equals(a, b));

        static Item NewMethod()
        {
            return new Item()
            {
                Tag = "RUNAANS_BOW",
                ExtraAttributes = new() { { "color", "0,255,0" }, { "runes", new Dictionary<object, object>() { { "GEM", 1 } } },
            { "modifier", "pure" }, { "uid", "0516172d4e55" }, { "uuid", "89ebd0e2-0572-4a7c-bcc3-0516172d4e55" }, { "anvil_uses", 2 }, { "timestamp", "8/9/19 2:42 PM" } }
            };
        }
    }
    [Test]
    public void CompareDrillWithDifferentFuel()
    {
        var comparer = new ItemCompare();
        var a = new Item()
        {
            Tag = "MITHRIL_DRILL_2",
            ExtraAttributes = new() { 
                { "rarity_upgrades", 1 }, 
                { "drill_part_upgrade_module", "goblin_omelette" }, 
                { "drill_part_engine", "mithril_drill_engine" }, 
                { "drill_fuel", 6464 },
                { "modifier", "fleet" }, 
                { "uid", "9b43ab629e84" }, 
                { "drill_part_fuel_tank", "mithril_fuel_tank" }, 
                { "uuid", "b528528d-ce3d-4daa-9ad8-9b43ab629e84" }, 
                { "timestamp", 1766804897464 }, 
                { "tier", 4 } 
            },
            Enchantments = new() { { "efficiency", 5 }, { "fortune", 3 }, { "ultimate_flowstate", 3 }, { "experience", 3 } }
        };
        var b = new Item()
        {
            Tag = "MITHRIL_DRILL_2",
            ExtraAttributes = new() { 
                { "rarity_upgrades", 1 }, 
                { "drill_part_fuel_tank", "mithril_fuel_tank" }, 
                { "uuid", "b528528d-ce3d-4daa-9ad8-9b43ab629e84" }, 
                { "drill_fuel", 6532 },
                { "drill_part_upgrade_module", "goblin_omelette" }, 
                { "drill_part_engine", "mithril_drill_engine" }, 
                { "uid", "9b43ab629e84" }, 
                { "modifier", "fleet" }, 
                { "timestamp", 1766804897464 }, 
                { "tier", 4 } 
            },
            Enchantments = new() { { "efficiency", 5 }, { "experience", 3 }, { "fortune", 3 }, { "ultimate_flowstate", 3 } }
        };
        // Items should be considered equal because drill_fuel is volatile and should be ignored
        Assert.That(comparer.Equals(a, b), "Items with same UUID/Tag but different drill_fuel should be equal");
    }
}
#nullable restore