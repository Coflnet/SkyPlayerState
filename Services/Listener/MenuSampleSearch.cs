using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Services;

/// <summary>One matching slot of a search. <see cref="SampleIndex"/> is the position in the sample list of the chest name (opaque, no player identity).</summary>
public class MenuSlotMatch
{
    public int SampleIndex { get; set; }
    public int Slot { get; set; }
    public string? Tag { get; set; }
    public string? ItemName { get; set; }
    /// <summary>Which of <c>itemName</c>, <c>tag</c>, <c>description</c> contained the text.</summary>
    public List<string> MatchedIn { get; set; } = [];
    /// <summary>A bit of the description around the hit (colour codes stripped), for context.</summary>
    public string? Snippet { get; set; }
}

public class MenuSearchResult
{
    public string Name { get; set; } = "";
    /// <summary>The chest name itself contains the text.</summary>
    public bool NameMatch { get; set; }
    public List<MenuSlotMatch> Matches { get; set; } = [];
}

/// <summary>Case-insensitive text search over chest name, item name, item tag and description (colour codes stripped).</summary>
public static class MenuSampleSearch
{
    public const int MaxMatchesPerName = 10;
    private const int SnippetRadius = 40;

    /// <summary>Null when neither the name nor a slot matches.</summary>
    public static MenuSearchResult? Match(string name, IReadOnlyList<MenuSample> samples, string query)
    {
        var result = new MenuSearchResult { Name = name, NameMatch = Contains(name, query) };
        for (var s = 0; s < samples.Count && result.Matches.Count < MaxMatchesPerName; s++)
        {
            foreach (var item in samples[s].Items)
            {
                var matched = new List<string>(3);
                var itemName = item.Name == null ? null : MenuTitleNormalizer.StripColors(item.Name);
                var description = item.Description == null ? null : MenuTitleNormalizer.StripColors(item.Description);
                if (Contains(itemName, query))
                    matched.Add("itemName");
                if (Contains(item.Tag, query))
                    matched.Add("tag");
                var at = description?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1;
                if (at >= 0)
                    matched.Add("description");
                if (matched.Count == 0)
                    continue;
                result.Matches.Add(new MenuSlotMatch
                {
                    SampleIndex = s,
                    Slot = item.Slot,
                    Tag = item.Tag,
                    ItemName = itemName,
                    MatchedIn = matched,
                    Snippet = at >= 0 ? Snippet(description!, at, query.Length) : null
                });
                if (result.Matches.Count >= MaxMatchesPerName)
                    break;
            }
        }
        return result.NameMatch || result.Matches.Count > 0 ? result : null;
    }

    private static bool Contains(string? text, string query)
        => text != null && text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string Snippet(string text, int at, int length)
    {
        var from = Math.Max(0, at - SnippetRadius);
        var to = Math.Min(text.Length, at + length + SnippetRadius);
        return text[from..to].Replace('\n', ' ');
    }
}
