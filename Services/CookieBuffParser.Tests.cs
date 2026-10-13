using System;
using AwesomeAssertions;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Services;

public class CookieBuffParserTests
{
    [Test]
    public void ActiveWithDaysHoursMinutes()
    {
        CookieBuffParser.TryParse("Server: mini1\n\nCookie Buff\n3d 4h 12m\n\nUpgrades", out var active, out var rem).Should().BeTrue();
        active.Should().BeTrue();
        rem.Should().Be(new TimeSpan(3, 4, 12, 0));
    }

    [Test]
    public void ActiveOnlyMinutes()
    {
        CookieBuffParser.TryParse("Cookie Buff\n45m", out var active, out var rem).Should().BeTrue();
        active.Should().BeTrue();
        rem.Should().Be(TimeSpan.FromMinutes(45));
    }

    [Test]
    public void NotActive()
    {
        CookieBuffParser.TryParse("Cookie Buff\nNot active! Obtain booster cookies from the community shop in the hub.\n\nx", out var active, out var rem).Should().BeTrue();
        active.Should().BeFalse();
        rem.Should().Be(TimeSpan.Zero);
    }

    [Test]
    public void MissingSection()
    {
        CookieBuffParser.TryParse("Server: mini1\n3d 4h", out _, out _).Should().BeFalse();
        CookieBuffParser.TryParse("", out _, out _).Should().BeFalse();
    }

    [Test]
    public void ColorCodesAreStripped()
    {
        CookieBuffParser.TryParse("§d§lCookie Buff\n§f2d 1h 5m 3s\n", out var active, out var rem).Should().BeTrue();
        active.Should().BeTrue();
        rem.Should().Be(new TimeSpan(2, 1, 5, 3));
    }

    [Test]
    public void DoesNotReadDurationsPastBlankLine()
    {
        CookieBuffParser.TryParse("Cookie Buff\n\n5m", out _, out _).Should().BeFalse();
    }
}
