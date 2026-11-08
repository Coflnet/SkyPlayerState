using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Coflnet.Sky.PlayerState.Tasks.Gear;

/// <summary>A profile together with the binding that applies it to one task.</summary>
public record BoundGearProfile(GearProfile Profile, GearBinding Binding);

/// <summary>
/// The researched gear profiles, embedded from Services/Tasks/Gear/profiles/*.json (see SkyPlayerState.csproj).
/// Profiles failing <see cref="GearProfileValidator.Validate"/> are skipped and listed in <see cref="Errors"/>
/// instead of breaking the endpoint; the catalog tests require that list to be empty.
/// </summary>
public sealed class GearProfileCatalog
{
    /// <summary>Logical name prefix of the embedded profile files, see the EmbeddedResource item in the csproj.</summary>
    internal const string ResourcePrefix = "Coflnet.Sky.PlayerState.Tasks.Gear.profiles.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly Lazy<GearProfileCatalog> LazyEmbedded = new(LoadEmbedded);

    /// <summary>The profiles shipped with the service.</summary>
    public static GearProfileCatalog Embedded => LazyEmbedded.Value;

    private readonly Dictionary<string, BoundGearProfile> byTask = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<GearProfile> Profiles { get; }
    /// <summary>Why a file or binding was skipped; empty for a healthy catalog.</summary>
    public IReadOnlyList<string> Errors { get; }
    /// <summary>Names of the files the catalog was loaded from.</summary>
    public IReadOnlyList<string> SourceNames { get; }

    private GearProfileCatalog(List<GearProfile> profiles, List<string> errors, List<string> sourceNames)
    {
        Profiles = profiles;
        Errors = errors;
        SourceNames = sourceNames;
        IndexBindings(profiles, errors);
    }

    /// <summary>Every (profile, binding) pair, one per bound task.</summary>
    public IEnumerable<BoundGearProfile> Bindings => byTask.Values;

    public bool TryGet(string taskName, out BoundGearProfile bound)
    {
        bound = null!;
        return taskName != null && byTask.TryGetValue(taskName, out bound!);
    }

    /// <summary>Parses and validates profile files given as (name, json) pairs.</summary>
    public static GearProfileCatalog FromJson(IEnumerable<(string Name, string Json)> files)
    {
        var profiles = new List<GearProfile>();
        var errors = new List<string>();
        var names = new List<string>();
        foreach (var (name, json) in files)
        {
            names.Add(name);
            var profile = Parse(name, json, errors);
            if (profile == null)
                continue;
            AddIfValid(name, profile, profiles, errors);
        }
        return new GearProfileCatalog(profiles, errors, names);
    }

    private static GearProfile? Parse(string name, string json, List<string> errors)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<GearProfile>(json, JsonOptions);
            if (profile == null)
                errors.Add($"{name}: empty profile");
            return profile;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            errors.Add($"{name}: invalid json: {e.Message}");
            return null;
        }
    }

    private static void AddIfValid(string name, GearProfile profile, List<GearProfile> profiles, List<string> errors)
    {
        var problems = SafeValidate(profile);
        if (profiles.Any(p => p.Id == profile.Id))
            problems.Add($"profile '{profile.Id}': id is used by another file");
        if (problems.Count > 0)
        {
            errors.AddRange(problems.Select(p => $"{name}: {p}"));
            return;
        }
        profiles.Add(profile);
    }

    /// <summary>A validator bug must cost one profile, never the whole (cached) catalog.</summary>
    private static List<string> SafeValidate(GearProfile profile)
    {
        try
        {
            return GearProfileValidator.Validate(profile);
        }
        catch (Exception e)
        {
            return [$"profile '{profile.Id}': validation failed: {e.GetType().Name}: {e.Message}"];
        }
    }

    private void IndexBindings(List<GearProfile> profiles, List<string> errors)
    {
        foreach (var profile in profiles)
        foreach (var binding in profile.Bindings)
        {
            if (byTask.TryGetValue(binding.Task, out var existing))
            {
                errors.Add($"task '{binding.Task}' is bound by profiles '{existing.Profile.Id}' and '{profile.Id}', keeping the first");
                continue;
            }
            byTask[binding.Task] = new BoundGearProfile(profile, binding);
        }
    }

    private static GearProfileCatalog LoadEmbedded()
    {
        var assembly = typeof(GearProfileCatalog).Assembly;
        var files = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (n, ReadResource(assembly, n)))
            .ToList();
        return FromJson(files);
    }

    private static string ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"embedded resource {name} missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
