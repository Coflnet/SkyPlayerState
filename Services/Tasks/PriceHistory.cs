using System;
using System.Collections.Generic;
using System.Linq;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Smooths the sniper clean prices over the last fetches. Load balanced sniper pods answer with different
/// (30 minute cached) prices, so a single fetch made every pod value the same aggregate differently.
/// The smoothed price is the per tag median of the last <see cref="MaxSamples"/> fetches, a tag missing
/// from a fetch keeps its value for <see cref="MissingTagLifetime"/>, a new tag is used at once.
/// Not thread safe, callers serialize the fetches.
/// </summary>
public class PriceHistory
{
    public const int MaxSamples = 15;
    public static readonly TimeSpan MissingTagLifetime = TimeSpan.FromHours(6);

    private readonly Dictionary<string, TagSamples> tags = new();

    private class TagSamples
    {
        public readonly Queue<double> Values = new();
        public DateTime LastSeen;
    }

    /// <summary>Records one fetch and returns the smoothed prices of every tag still alive.</summary>
    public Dictionary<string, double> Add(IReadOnlyDictionary<string, double> fetch, DateTime now)
    {
        Record(fetch, now);
        RemoveExpired(now);
        return tags.ToDictionary(t => t.Key, t => Median(t.Value.Values));
    }

    private void Record(IReadOnlyDictionary<string, double> fetch, DateTime now)
    {
        foreach (var item in fetch)
        {
            // the sniper omits tags without a price; a reported non-positive price is a real "not worth anything" signal, drop the history at once
            if (item.Value <= 0)
            {
                tags.Remove(item.Key);
                continue;
            }
            if (!tags.TryGetValue(item.Key, out var samples))
                tags[item.Key] = samples = new TagSamples();
            samples.Values.Enqueue(item.Value);
            while (samples.Values.Count > MaxSamples)
                samples.Values.Dequeue();
            samples.LastSeen = now;
        }
    }

    private void RemoveExpired(DateTime now)
    {
        var expired = tags.Where(t => now - t.Value.LastSeen > MissingTagLifetime).Select(t => t.Key).ToList();
        foreach (var tag in expired)
            tags.Remove(tag);
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        // lower median: always a price that was observed, never an average of two
        return sorted[(sorted.Count - 1) / 2];
    }
}
