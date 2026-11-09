using System;
using System.Collections.Generic;
using System.Linq;
using Coflnet.Sky.PlayerState.Tasks;

namespace Coflnet.Sky.PlayerState.Services.Gear;

/// <summary>
/// The block kind a mining task mines and the fortune subtype that adds to Mining Fortune on it (patch 0.20.6, wiki
/// Mining_Fortune, Ore_Fortune, Dwarven_Metal_Fortune, Block_Fortune, Ores, Blocks; research-gear-effects-2.md):
/// <list type="bullet">
/// <item>ore (Ore Fortune): coal, iron, gold, lapis, redstone, emerald, diamond, quartz, sulphur;</item>
/// <item>dwarven metal (Dwarven Metal Fortune): mithril, titanium, umber, tungsten, glacite (no ores since 0.20.6);</item>
/// <item>block (Block Fortune): cobblestone, stone, end stone, obsidian, netherrack, ice, glowstone, red sand, mycelium,
/// sand, gravel, hard stone.</item>
/// </list>
/// A task's kind comes from its DetectionItems (ENCHANTED_ prefixes and _ORE/_INGOT/_BLOCK suffixes stripped) when they
/// all agree, else from its name ("Umber Mining"); gemstone tasks (Gemstone Fortune is their own formula) have none.
/// </summary>
public static class MiningBlocks
{
    public const string Ore = "ore";
    public const string DwarvenMetal = "dwarven-metal";
    public const string Block = "block";

    public sealed record Kind(string Id, string FortuneStat);

    public static readonly Kind OreKind = new(Ore, "Ore Fortune");
    public static readonly Kind DwarvenMetalKind = new(DwarvenMetal, "Dwarven Metal Fortune");
    public static readonly Kind BlockKind = new(Block, "Block Fortune");
    public static readonly IReadOnlyList<Kind> All = [OreKind, DwarvenMetalKind, BlockKind];

    private static readonly Dictionary<string, Kind> ByMaterial = Materials(
        (OreKind, ["COAL", "IRON", "GOLD", "LAPIS", "LAPIS_LAZULI", "INK_SACK:4", "REDSTONE", "EMERALD", "DIAMOND", "QUARTZ", "SULPHUR"]),
        (DwarvenMetalKind, ["MITHRIL", "TITANIUM", "UMBER", "TUNGSTEN", "GLACITE"]),
        (BlockKind, ["COBBLESTONE", "STONE", "END_STONE", "ENDSTONE", "ENDER_STONE", "OBSIDIAN", "NETHERRACK", "ICE", "GLOWSTONE",
            "RED_SAND", "MYCELIUM", "SAND", "GRAVEL", "HARD_STONE"]));

    private static readonly string[] Suffixes = ["_BLOCK", "_ORE", "_INGOT", "_DUST"];
    private static readonly Lazy<Dictionary<string, Kind>> ByTask = new(LoadTasks);

    /// <summary>The kind of a registered mining task, null for other tasks.</summary>
    public static Kind? ForTask(string? task) => task != null && ByTask.Value.TryGetValue(task, out var kind) ? kind : null;

    /// <summary>The kind of a kind id ("ore"), null for an unknown id.</summary>
    public static Kind? Of(string? id) => All.FirstOrDefault(k => k.Id == id);

    /// <summary>The kind all <paramref name="items"/> agree on, else the kind of the task name's material; null without one.</summary>
    public static Kind? Resolve(string taskName, IEnumerable<string> items)
    {
        var kinds = items.Select(OfItem).Distinct().ToList();
        if (kinds.Count == 1 && kinds[0] != null)
            return kinds[0];
        return OfItem(NameMaterial(taskName));
    }

    /// <summary>"ENCHANTED_MITHRIL", "MITHRIL_ORE" and "ENCHANTED_GOLD_BLOCK" give their material's kind.</summary>
    public static Kind? OfItem(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
            return null;
        var material = tag.ToUpperInvariant();
        if (material.StartsWith("ENCHANTED_", StringComparison.Ordinal))
            material = material["ENCHANTED_".Length..];
        var suffix = Suffixes.FirstOrDefault(s => material.EndsWith(s, StringComparison.Ordinal));
        if (suffix != null)
            material = material[..^suffix.Length];
        return ByMaterial.GetValueOrDefault(material);
    }

    /// <summary>"End Stone Mining" -> END_STONE; null for a name not ending in " Mining".</summary>
    private static string? NameMaterial(string taskName)
    {
        const string suffix = " Mining";
        if (!taskName.EndsWith(suffix, StringComparison.Ordinal))
            return null;
        return taskName[..^suffix.Length].Trim().Replace(' ', '_').ToUpperInvariant();
    }

    private static Dictionary<string, Kind> LoadTasks()
    {
        var result = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in new TaskRegistry().Tasks.OfType<BaseMiningTask>())
        {
            var signature = task.GetDetectionSignature();
            if (Resolve(signature.MethodName, signature.DetectionItems ?? []) is { } kind)
                result.TryAdd(signature.MethodName, kind);
        }
        return result;
    }

    private static Dictionary<string, Kind> Materials(params (Kind Kind, string[] Materials)[] groups)
        => groups.SelectMany(g => g.Materials.Select(m => (Material: m, g.Kind)))
            .ToDictionary(m => m.Material, m => m.Kind, StringComparer.Ordinal);
}
