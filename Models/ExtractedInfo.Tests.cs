using System;
using System.Linq;
using System.Reflection;
using AwesomeAssertions;
using MessagePack;
using NUnit.Framework;

namespace Coflnet.Sky.PlayerState.Models;

public class ExtractedInfoTests
{
    /// <summary>
    /// Two properties sharing a MessagePack key silently overwrite each other in the persisted state
    /// (2026-10-02: LastTransferViewAt and LastKuudraTier were both added as Key 40 in parallel).
    /// </summary>
    [Test]
    public void MessagePackKeysAreUnique()
    {
        var duplicates = typeof(ExtractedInfo).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => (p.Name, Key: p.GetCustomAttribute<KeyAttribute>()?.IntKey))
            .Where(p => p.Key.HasValue)
            .GroupBy(p => p.Key.Value)
            .Where(g => g.Count() > 1)
            .Select(g => $"Key {g.Key}: {string.Join(", ", g.Select(p => p.Name))}");

        duplicates.Should().BeEmpty();
    }

    [Test]
    public void TransferViewAndKuudraTierSurviveARoundTrip()
    {
        var seenAt = new DateTime(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc);
        var info = new ExtractedInfo
        {
            LastTransferViewAt = seenAt,
            LastKuudraTier = "Kuudra's Hollow (T5)",
            LastKuudraTierAt = seenAt.AddMinutes(-5)
        };

        var copy = MessagePackSerializer.Deserialize<ExtractedInfo>(MessagePackSerializer.Serialize(info));

        copy.LastTransferViewAt.Should().Be(seenAt);
        copy.LastKuudraTier.Should().Be("Kuudra's Hollow (T5)");
        copy.LastKuudraTierAt.Should().Be(seenAt.AddMinutes(-5));
    }
}
