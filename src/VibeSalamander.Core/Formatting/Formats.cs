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

    /// <summary>A file size in the chosen format (Configuration → Panels → Show sizes).</summary>
    public static string Size(long bytes, Settings.SizeFormat format) => format switch
    {
        Settings.SizeFormat.KiloBytes => ((bytes + 1023) / 1024).ToString("N0", CultureInfo.CurrentCulture) + " KB",
        Settings.SizeFormat.Short => ShortSize(bytes),
        _ => ExactSize(bytes),
    };

    /// <summary>A name in the chosen case (Configuration → Panels → Show names); display only.</summary>
    public static string ApplyCasing(string name, bool isDirectory, Settings.NameCasing casing)
    {
        if (name.Length == 0 || name == "..")
            return name;
        CultureInfo culture = CultureInfo.CurrentCulture;
        string Mixed(string text)
        {
            int dot = isDirectory ? -1 : text.LastIndexOf('.');
            string body = dot > 0 ? text[..dot] : text;
            string ext = dot > 0 ? text[dot..] : string.Empty;
            body = body.ToLower(culture);
            return char.ToUpper(body[0], culture) + body[1..] + ext.ToLower(culture);
        }
        return casing switch
        {
            Settings.NameCasing.MixedIfDos => IsDosName(name) ? Mixed(name) : name,
            Settings.NameCasing.LowerFilesUpperDirectories => isDirectory ? name.ToUpper(culture) : name.ToLower(culture),
            Settings.NameCasing.PartiallyMixed => Mixed(name),
            Settings.NameCasing.Lower => name.ToLower(culture),
            Settings.NameCasing.Upper => name.ToUpper(culture),
            _ => name,
        };
    }

    /// <summary>"README.TXT": all upper case and 8.3.</summary>
    private static bool IsDosName(string name)
    {
        if (name != name.ToUpperInvariant())
            return false;
        int dot = name.LastIndexOf('.');
        string body = dot >= 0 ? name[..dot] : name;
        string ext = dot >= 0 ? name[(dot + 1)..] : string.Empty;
        return body.Length is > 0 and <= 8 && ext.Length <= 3 && name.Count(c => c == '.') <= 1;
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
