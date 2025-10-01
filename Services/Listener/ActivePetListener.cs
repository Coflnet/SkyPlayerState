using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Coflnet.Sky.PlayerState.Models;
using static Coflnet.Sky.PlayerState.Models.ExtractedInfo;
using System.Collections.Generic;

namespace Coflnet.Sky.PlayerState.Services;

public class ActivePetListener : UpdateListener
{
    private static readonly Regex ColorCodeRegex = new("§.", RegexOptions.Compiled);
    private const string SelectedPetMarker = "Selected pet:";
    private const string ProgressMarker = "Progress to Level";

    public override Task Process(UpdateArgs args)
    {
        if (args.msg.Chest?.Name != "Pets")
            return Task.CompletedTask;

        var items = args.msg.Chest.Items ?? new();
        var pets = new List<PetState>();
        PetState? activePet = null;

        foreach (var item in items)
        {
            if (item?.Tag != null && item.Tag.StartsWith("PET_"))
            {
                var petState = ParsePetState(item);
                if (petState != null)
                {
                    pets.Add(petState);
                    if (petState.IsActive)
                        activePet = petState;
                }
            }
        }

        args.currentState.ExtractedInfo.Pets = pets;
        args.currentState.ExtractedInfo.ActivePet = activePet is not null ? new ActivePet
        {
            Name = activePet.Name,
            ColorCode = activePet.ColorCode,
            ProgressPercent = activePet.ProgressPercent,
            TargetLevel = activePet.TargetLevel,
            LastUpdated = DateTime.UtcNow
        } : null;
        return Task.CompletedTask;
    }

    public PetState? ParsePetState(Item item)
    {
        if (item == null || item.ExtraAttributes == null || !item.ExtraAttributes.TryGetValue("petInfo", out var petInfoObj))
            return null;

        var petInfo = petInfoObj as Newtonsoft.Json.Linq.JObject;
        if (petInfo == null)
            return null;

        var name = StripColorCodes(item.ItemName ?? "");
        var colorCode = item.ItemName?.Length >= 2 && item.ItemName.StartsWith('§') ? item.ItemName[..2] : null;
        var type = petInfo.Value<string>("type");
        var tier = petInfo.Value<string>("tier");
        var level = ParseLevelFromName(item.ItemName);
        var exp = petInfo.Value<double?>("exp") ?? 0;
        var isActive = petInfo.Value<bool?>("active") ?? false;
        var heldItem = petInfo.Value<string>("heldItem");
        var candyUsed = petInfo.Value<int?>("candyUsed") ?? 0;
        var tag = item.Tag;
        var uuid = petInfo.Value<string>("uuid") ?? petInfo.Value<string>("uniqueId");

        // Parse progress, target level, current exp, exp for level from description
        double progressPercent = 0;
        int targetLevel = 0;
        double currentExp = 0;
        double expForLevel = 0;
        if (!string.IsNullOrWhiteSpace(item.Description))
        {
            var lines = item.Description.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var progressLine = lines.FirstOrDefault(l => l.Contains("Progress to Level", StringComparison.OrdinalIgnoreCase));
            if (progressLine != null)
            {
                var normalized = ColorCodeRegex.Replace(progressLine, string.Empty);
                TryParseTargetLevel(normalized, out targetLevel);
                TryParsePercent(normalized, out progressPercent);
            }
            var expLine = lines.FirstOrDefault(l => l.Contains("/"));
            if (expLine != null)
            {
                var expParts = ColorCodeRegex.Replace(expLine, string.Empty).Split('/');
                if (expParts.Length == 2)
                {
                    double.TryParse(expParts[0].Replace(",", "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out currentExp);
                    double.TryParse(expParts[1].Replace(",", "").Trim().Replace("k", "000").Replace("M", "000000"), NumberStyles.Float, CultureInfo.InvariantCulture, out expForLevel);
                }
            }
        }

        return new PetState
        {
            Name = name,
            Type = type,
            Tier = tier,
            Level = level,
            Exp = exp,
            IsActive = isActive,
            HeldItem = heldItem,
            CandyUsed = candyUsed,
            ColorCode = colorCode,
            Tag = tag,
            Uuid = uuid,
            ProgressPercent = progressPercent,
            TargetLevel = targetLevel,
            CurrentExp = currentExp,
            ExpForLevel = expForLevel,
            LastUpdated = DateTime.UtcNow
        };
    }

    private static int ParseLevelFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;
        var match = Regex.Match(name, @"\[Lvl (\d+)\]");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static string StripColorCodes(string input)
    {
        return ColorCodeRegex.Replace(input, string.Empty).Trim();
    }

    public ActivePet? ParseActivePet(Item petMenuItem)
    {
        if (string.IsNullOrWhiteSpace(petMenuItem.Description))
            return null;

        var lines = petMenuItem.Description.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var selectedLine = lines.FirstOrDefault(l => l.Contains(SelectedPetMarker, StringComparison.OrdinalIgnoreCase));
        if (selectedLine == null)
            return null;

        var afterMarker = ExtractSegment(selectedLine, SelectedPetMarker);
        if (string.IsNullOrWhiteSpace(afterMarker))
            return null;

        var colorCode = afterMarker.StartsWith('§') && afterMarker.Length >= 2 ? afterMarker[..2] : null;
        var displayName = ColorCodeRegex.Replace(afterMarker, string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName, "None", StringComparison.OrdinalIgnoreCase))
            return null;

        var activePet = new ActivePet
        {
            Name = displayName,
            ColorCode = colorCode
        };

        var progressLine = lines.FirstOrDefault(l => l.Contains(ProgressMarker, StringComparison.OrdinalIgnoreCase));
        if (progressLine != null)
        {
            var normalizedProgress = ColorCodeRegex.Replace(progressLine, string.Empty);
            if (TryParseTargetLevel(normalizedProgress, out var level))
            {
                activePet.TargetLevel = level;
            }

            if (TryParsePercent(normalizedProgress, out var percent))
            {
                activePet.ProgressPercent = percent;
            }
        }

        return activePet;
    }

    private static string ExtractSegment(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return string.Empty;
        return source[(index + marker.Length)..].TrimStart();
    }

    private static bool TryParseTargetLevel(string progressLine, out int targetLevel)
    {
        targetLevel = 0;
        var markerIndex = progressLine.IndexOf(ProgressMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return false;

        var afterMarker = progressLine[(markerIndex + ProgressMarker.Length)..].TrimStart();
        var colonIndex = afterMarker.IndexOf(':');
        if (colonIndex < 0)
            return false;

        var levelSegment = afterMarker[..colonIndex].Trim();
        var digits = new string(levelSegment.Where(char.IsDigit).ToArray());
        return !string.IsNullOrEmpty(digits) && int.TryParse(digits, out targetLevel);
    }

    private static bool TryParsePercent(string progressLine, out double percent)
    {
        percent = 0;
        var percentIndex = progressLine.IndexOf('%');
        if (percentIndex < 0)
            return false;

        var start = percentIndex;
        while (start > 0 && (char.IsDigit(progressLine[start - 1]) || progressLine[start - 1] == '.' || progressLine[start - 1] == ','))
        {
            start--;
        }

        var numericSpan = progressLine[start..percentIndex].Trim();
        if (string.IsNullOrEmpty(numericSpan))
            return false;

        var sanitized = numericSpan.Replace(",", string.Empty);
        return double.TryParse(sanitized, NumberStyles.Float, CultureInfo.InvariantCulture, out percent);
    }
}
