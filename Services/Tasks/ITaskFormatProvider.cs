using System;
using System.Globalization;

namespace Coflnet.Sky.PlayerState.Tasks;

/// <summary>
/// Provides formatting methods for prices and times used in task messages.
/// </summary>
public interface ITaskFormatProvider
{
    string FormatPrice(double price);
    string FormatTime(TimeSpan time);
}

/// <summary>
/// Default format provider, produces the short human readable formats
/// the mod displays (5.2M, 3.4h).
/// </summary>
public class SimpleTaskFormatProvider : ITaskFormatProvider
{
    public string FormatPrice(double price) => FormatPriceShort(price);
    public string FormatTime(TimeSpan time) => FormatTimeGlobal(time);

    /// <summary>
    /// Format a number to a short string with a suffix (K, M, B),
    /// mirror of the mod side FormatProvider.FormatPriceShort.
    /// </summary>
    public static string FormatPriceShort(double num)
    {
        if (num == 0) // there was an issue with flips attempting to be devided by 0
            return "0";
        var minusPrefix = num < 0 ? "-" : "";
        num = Math.Abs(num);
        // Ensure number has max 3 significant digits (no rounding up can happen)
        long i = (long)Math.Pow(10, (long)Math.Max(0, Math.Log10(num) - 2));
        var roundNum = (long)num / i * i;

        if (num >= 1000000000)
            return Format(1000000000, "B");
        if (num > 1000000 - 1)
            return Format(1000000, "M");
        if (num >= 1000)
            return Format(1000, "K");

        return minusPrefix + num.ToString("0.#", CultureInfo.InvariantCulture);

        string Format(double devider, string suffix, string format = "0.##")
        {
            return minusPrefix + (roundNum / devider).ToString(format, CultureInfo.InvariantCulture) + suffix;
        }
    }

    /// <summary>
    /// Mirror of the mod side FormatProvider.FormatTimeGlobal.
    /// </summary>
    public static string FormatTimeGlobal(TimeSpan timeSpan)
    {
        var prefix = timeSpan.TotalSeconds < 0 ? "-" : "";
        timeSpan = timeSpan.Duration();
        if (timeSpan.TotalDays > 1.05)
            return $"{timeSpan.TotalDays:0.#}d";
        if (timeSpan.TotalHours > 1)
            return $"{timeSpan.TotalHours:0.#}h";
        if (timeSpan.TotalMinutes > 1)
            return $"{timeSpan.TotalMinutes:0.#}m";
        return $"{prefix}{timeSpan.TotalSeconds:0.#}s";
    }
}
