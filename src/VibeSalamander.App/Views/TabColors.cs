// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Windows.UI;

namespace VibeSalamander.Views;

/// <summary>Color arithmetic for the tabs: laying one color over another, and telling bright from dark.</summary>
internal static class TabColors
{
    /// <summary><paramref name="top"/> laid over <paramref name="under"/> (both may be see-through).</summary>
    public static Color Over(Color under, Color top)
    {
        double ta = top.A / 255.0, ua = under.A / 255.0;
        double a = ta + ua * (1 - ta);
        if (a <= 0)
            return Color.FromArgb(0, 0, 0, 0);
        byte Mix(byte t, byte u) => (byte)Math.Round((t * ta + u * ua * (1 - ta)) / a);
        return Color.FromArgb((byte)Math.Round(a * 255), Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    /// <summary>Bright enough for dark text (relative luminance over 0.5, by the sRGB curve).</summary>
    public static bool IsBright(Color c)
    {
        static double Linear(byte v)
        {
            double s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        double luminance = 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
        return luminance > 0.18; // 0.18 linear ≈ 0.5 perceived (mid grey)
    }

    /// <summary>The same color, more opaque.</summary>
    public static Color Stronger(Color c, double factor) => Color.FromArgb((byte)Math.Min(255, Math.Round(c.A * factor)), c.R, c.G, c.B);
}
