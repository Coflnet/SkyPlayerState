using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>
/// Listener that detects items mentioning Mythological Ritual in their description
/// </summary>
public partial class MythologicalRitualListener : UpdateListener
{
    private IMythologicalRitualService? _service;

    public override async Task Process(UpdateArgs args)
    {
        _service ??= args.GetService<IMythologicalRitualService>();

        if (args.msg.Chest?.Items == null)
            return;

        foreach (var item in args.msg.Chest.Items)
        {
            if (string.IsNullOrEmpty(item.Tag) || string.IsNullOrEmpty(item.Description))
                continue;

            // Skip if already known in-memory
            if (_service.IsKnownTag(item.Tag))
                continue;

            // Check if description contains "Mythological Ritual" ignoring color codes and newlines
            if (ContainsMythologicalRitual(item.Description))
            {
                await _service.TryStoreTag(item.Tag, item.Description, args.currentState.McInfo?.Name ?? "unknown");
            }
        }
    }

    /// <summary>
    /// Checks if the description contains "Mythological Ritual" ignoring color codes (§x) and newlines
    /// </summary>
    public static bool ContainsMythologicalRitual(string description)
    {
        if (string.IsNullOrEmpty(description))
            return false;
        if(!description.Contains("Mythological"))
            return false; // shortcut most actually don't mention it
        // Remove color codes (§ followed by any character) and newlines, then check for the phrase
        var normalized = NormalizeText(description);
        return normalized.Contains("Mythological Ritual", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes text by removing Minecraft color codes and replacing newlines with spaces
    /// </summary>
    public static string NormalizeText(string text)
    {
        // Remove §x color codes and replace newlines with spaces to handle wrapped text
        return ColorCodeRegex().Replace(text, "").Replace("\n", " ");
    }

    [GeneratedRegex(@"§.")]
    private static partial Regex ColorCodeRegex();

    public override Task Load(CancellationToken token)
    {
        return Task.CompletedTask;
    }
}
