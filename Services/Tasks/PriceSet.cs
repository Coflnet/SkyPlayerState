using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>Merged prices of one fetch. Incomplete when a source has no price data at all (not even a last good one).</summary>
public sealed class PriceSet
{
    public PriceSet(Dictionary<string, double> prices, bool isComplete, DateTime fetchedAt, DateTime checkedAt, TimeSpan cacheTtl, PriceFingerprint fingerprint)
    {
        CheckedAt = checkedAt;
        Prices = prices;
        IsComplete = isComplete;
        FetchedAt = fetchedAt;
        CacheTtl = cacheTtl;
        Fingerprint = fingerprint;
    }

    public Dictionary<string, double> Prices { get; }
    public bool IsComplete { get; }
    /// <summary>The older of the two sources' last successful fetches, so an outage shows as age.</summary>
    public DateTime FetchedAt { get; }
    /// <summary>When this set was last (re)evaluated; the cache TTL counts from here.</summary>
    internal DateTime CheckedAt { get; }
    internal TimeSpan CacheTtl { get; }
    public PriceFingerprint Fingerprint { get; }

    /// <summary>True when something other than the copper pseudo price is known.</summary>
    public bool HasMarketPrices => Prices.Keys.Any(k => k != PseudoItems.COPPER);
}

/// <summary>Identifies a price list: pods with the same hash value items alike.</summary>
public record PriceFingerprint(int TagCount, string Hash, DateTime FetchedAt)
{
    public double HashAsNumber => long.Parse(Hash[..12], System.Globalization.NumberStyles.HexNumber);

    public static PriceFingerprint Of(IReadOnlyDictionary<string, double> prices, DateTime fetchedAt)
    {
        var text = string.Join(';', (prices ?? new Dictionary<string, double>())
            .OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
        return new PriceFingerprint(prices?.Count ?? 0, hash, fetchedAt);
    }
}

/// <summary>Thrown by consumers that cannot answer without complete prices; endpoints map it to 503.</summary>
public class PricesUnavailableException : Exception
{
    public PricesUnavailableException() : base("Prices are not available yet, retry shortly") { }
}
