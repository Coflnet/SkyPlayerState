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
}
#nullable restore