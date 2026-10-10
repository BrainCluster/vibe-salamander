using System.Globalization;
using System.Text;

namespace VibeSalamander.Core.Formatting;

/// <summary>Salamander-style display formats: exact byte counts, short dates, attribute letters.</summary>
public static class Formats
{
    /// <summary>Exact size with thousands separators ("13,711,106,048"), as in Salamander's Size column.</summary>
    public static string ExactSize(long bytes) => bytes.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>Compact size ("514 GB", "1.34 TB", "980 KB").</summary>
    public static string ShortSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        string number = unit == 0 || value >= 100 ? value.ToString("0", CultureInfo.CurrentCulture)
                      : value >= 10 ? value.ToString("0.#", CultureInfo.CurrentCulture)
                      : value.ToString("0.##", CultureInfo.CurrentCulture);
        return number + " " + units[unit];
    }

    public static string Date(DateTime value) =>
        value == DateTime.MinValue ? string.Empty : value.ToString("d", CultureInfo.CurrentCulture);

    public static string Time(DateTime value) =>
        value == DateTime.MinValue ? string.Empty : value.ToString("T", CultureInfo.CurrentCulture);

    /// <summary>Attribute letters in Salamander order: R H S A, plus C (compressed), E (encrypted), O (offline).</summary>
    public static string Attributes(FileAttributes a)
    {
        var sb = new StringBuilder(6);
        if ((a & FileAttributes.ReadOnly) != 0) sb.Append('R');
        if ((a & FileAttributes.Hidden) != 0) sb.Append('H');
        if ((a & FileAttributes.System) != 0) sb.Append('S');
        if ((a & FileAttributes.Archive) != 0) sb.Append('A');
        if ((a & FileAttributes.Compressed) != 0) sb.Append('C');
        if ((a & FileAttributes.Encrypted) != 0) sb.Append('E');
        if ((a & FileAttributes.Offline) != 0) sb.Append('O');
        return sb.ToString();
    }
}
