// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.DiskMap;

/// <summary>A color with channels 0..1.</summary>
internal readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb From(uint rgb) => new(((rgb >> 16) & 0xFF) / 255.0, ((rgb >> 8) & 0xFF) / 255.0, (rgb & 0xFF) / 255.0);

    public static readonly Rgb White = new(1, 1, 1);

    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>This color moved toward <paramref name="other"/> by t (0 = this, 1 = other).</summary>
    public Rgb Mix(Rgb other, double t) => new(R + (other.R - R) * t, G + (other.G - G) * t, B + (other.B - B) * t);

    public Rgb Scale(double f) => new(R * f, G * f, B * f);

    /// <summary>Perceived brightness 0..1.</summary>
    public double Luminance => 0.2126 * R + 0.7152 * G + 0.0722 * B;

    /// <summary>0xAARRGGBB, opaque.</summary>
    public uint ToArgb() => 0xFF000000u | ((uint)Channel(R) << 16) | ((uint)Channel(G) << 8) | Channel(B);

    public static byte Channel(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
}

/// <summary>
/// The pixels of a view (premultiplied BGRA, transparent at first) with the few drawing operations the views
/// need: rectangles, rounded rectangles with smooth edges, and single pixels with coverage.
/// </summary>
internal sealed class Picture
{
    public Picture(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = new byte[Width * Height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>Paints <paramref name="color"/> over the pixel with the given coverage (0..1).</summary>
    public void Blend(int x, int y, Rgb color, double coverage)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height || coverage <= 0)
            return;
        int i = (y * Width + x) * 4;
        if (coverage >= 1)
        {
            Pixels[i] = Rgb.Channel(color.B);
            Pixels[i + 1] = Rgb.Channel(color.G);
            Pixels[i + 2] = Rgb.Channel(color.R);
            Pixels[i + 3] = 255;
            return;
        }
        // "over": the new color with its coverage, then what was there
        double keep = 1 - coverage;
        Pixels[i] = (byte)Math.Clamp((int)Math.Round(color.B * 255 * coverage + Pixels[i] * keep), 0, 255);
        Pixels[i + 1] = (byte)Math.Clamp((int)Math.Round(color.G * 255 * coverage + Pixels[i + 1] * keep), 0, 255);
        Pixels[i + 2] = (byte)Math.Clamp((int)Math.Round(color.R * 255 * coverage + Pixels[i + 2] * keep), 0, 255);
        Pixels[i + 3] = (byte)Math.Clamp((int)Math.Round(255 * coverage + Pixels[i + 3] * keep), 0, 255);
    }

    /// <summary>An opaque pixel (no checks of coverage).</summary>
    public void Set(int x, int y, Rgb color)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;
        int i = (y * Width + x) * 4;
        Pixels[i] = Rgb.Channel(color.B);
        Pixels[i + 1] = Rgb.Channel(color.G);
        Pixels[i + 2] = Rgb.Channel(color.R);
        Pixels[i + 3] = 255;
    }

    /// <summary>
    /// A rectangle with rounded corners and smooth edges. <paramref name="shade"/> (optional) darkens or lightens
    /// each row: it gets the position 0 (top) .. 1 (bottom) and returns the factor for the color.
    /// </summary>
    public void FillRoundedRect(double x, double y, double w, double h, double radius, Rgb color, Func<double, double>? shade = null)
    {
        if (w <= 0 || h <= 0)
            return;
        radius = Math.Max(0, Math.Min(radius, Math.Min(w, h) / 2));
        int x0 = Math.Max(0, (int)Math.Floor(x)), x1 = Math.Min(Width, (int)Math.Ceiling(x + w));
        int y0 = Math.Max(0, (int)Math.Floor(y)), y1 = Math.Min(Height, (int)Math.Ceiling(y + h));
        double right = x + w, bottom = y + h;
        for (int py = y0; py < y1; py++)
        {
            double cy = py + 0.5;
            double rowCover = Math.Clamp(Math.Min(cy - y, bottom - cy) + 0.5, 0, 1);
            if (rowCover <= 0)
                continue;
            Rgb rowColor = shade == null ? color : Shade(color, shade((cy - y) / h));
            for (int px = x0; px < x1; px++)
            {
                double cx = px + 0.5;
                double cover = Math.Min(rowCover, Math.Clamp(Math.Min(cx - x, right - cx) + 0.5, 0, 1));
                if (radius > 0.5)
                {
                    // inside a corner square: the distance to the corner's circle decides
                    double dx = cx < x + radius ? x + radius - cx : cx > right - radius ? cx - (right - radius) : 0;
                    double dy = cy < y + radius ? y + radius - cy : cy > bottom - radius ? cy - (bottom - radius) : 0;
                    if (dx > 0 && dy > 0)
                        cover = Math.Min(cover, Math.Clamp(radius - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1));
                }
                Blend(px, py, rowColor, cover);
            }
        }
    }

    /// <summary>factor &gt; 1 lightens toward white, &lt; 1 darkens.</summary>
    public static Rgb Shade(Rgb color, double factor) =>
        factor >= 1 ? color.Mix(Rgb.White, Math.Min(1, factor - 1)) : color.Scale(Math.Max(0, factor));
}
