// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.DiskMap;

/// <summary>
/// Rings around the directory: each ring is one level deeper, each item an arc as wide as its share. Every
/// top-level directory has its own color, and everything inside it takes that color, fading with depth.
/// The middle is the directory itself (a click there goes up).
/// </summary>
internal sealed class SunburstView : IMapView
{
    public string Name => "Sunburst";

    private const int MaxRings = 7;

    private readonly record struct Arc(double Start, double End, Node Node, Rgb Color);

    private sealed class Geometry
    {
        public double CenterX, CenterY, Hole, RingWidth, Radius;
        public int Rings;
        public List<Arc>[] ByRing = Array.Empty<List<Arc>>();
        public Node Root = null!;
        public readonly Dictionary<Node, (int Ring, Arc Arc)> ByNode = new(ReferenceEqualityComparer.Instance);
    }

    public Frame Render(Node root, RenderContext context, CancellationToken cancellationToken)
    {
        var picture = new Picture(context.Width, context.Height);
        var frame = new Frame(picture);
        double s = context.Scale;
        var g = new Geometry
        {
            Root = root,
            CenterX = picture.Width / 2.0,
            CenterY = picture.Height / 2.0,
            Radius = Math.Max(10, Math.Min(picture.Width, picture.Height) / 2.0 - 10 * s),
        };
        g.Rings = Math.Clamp(DepthOf(root, MaxRings), 1, MaxRings);
        g.Hole = g.Radius * (g.Rings <= 2 ? 0.30 : 0.24);
        g.RingWidth = (g.Radius - g.Hole) / g.Rings;
        g.ByRing = new List<Arc>[g.Rings + 1];
        for (int i = 0; i <= g.Rings; i++)
            g.ByRing[i] = new List<Arc>();

        // the arcs: ring 1 = the directory's items, each in its own color
        double minArc = 0.8; // pixels at the outer edge of the ring: narrower arcs aren't drawn
        int hue = 0;
        double angle = 0;
        foreach (Node child in root.Children)
        {
            if (child.Size <= 0)
                break;
            double sweep = 2 * Math.PI * child.Size / Math.Max(1, root.Size);
            Rgb color = Layout.HueOf(hue++);
            AddArcs(g, child, angle, angle + sweep, 1, color, context, minArc);
            angle += sweep;
        }
        cancellationToken.ThrowIfCancellationRequested();
        Paint(picture, g, context, cancellationToken);

        frame.HitTest = p => HitTest(g, p);
        frame.Outline = n => Outline(g, n);

        // the middle: the directory and its size
        double hole = g.Hole * 1.5;
        frame.Labels.Add(new PluginLabel(g.CenterX - hole / 2, g.CenterY - 22 * s, hole, 44 * s,
            $"{ShortName(root)}\n{Layout.FormatSize(root.Size)}", context.Text, FontSize: 13, Bold: true, Centered: true));
        AddLabels(frame, g, context);

        int legend = 0;
        foreach (Node child in root.Children)
        {
            if (child.Size <= 0 || legend >= 10)
                break;
            frame.Legend.Add(new PluginLegendItem(Layout.HueOf(legend++).ToArgb(), $"{child.Name}  {Layout.FormatSize(child.Size)}"));
        }
        return frame;
    }

    private static string ShortName(Node node) =>
        node.Parent == null ? (Path.GetFileName(node.Name.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : node.Name) : node.Name;

    private static int DepthOf(Node node, int limit)
    {
        if (limit == 0 || !node.IsDirectory || node.Children.Length == 0)
            return 0;
        int deepest = 0;
        foreach (Node child in node.Children)
        {
            if (child.IsDirectory)
                deepest = Math.Max(deepest, DepthOf(child, limit - 1));
            if (deepest == limit - 1)
                break;
        }
        return deepest + 1;
    }

    private static void AddArcs(Geometry g, Node node, double start, double end, int ring, Rgb hue, RenderContext c, double minArc)
    {
        double outer = g.Hole + g.RingWidth * ring;
        if ((end - start) * outer < minArc)
            return;
        var arc = new Arc(start, end, node, Layout.BranchColor(hue, ring, !node.IsDirectory, c));
        g.ByRing[ring].Add(arc);
        g.ByNode[node] = (ring, arc);
        if (ring >= g.Rings || !node.IsDirectory || node.Size <= 0)
            return;
        double angle = start;
        foreach (Node child in node.Children)
        {
            if (child.Size <= 0)
                break;
            double sweep = (end - start) * child.Size / node.Size;
            if (sweep * (outer + g.RingWidth) < minArc)
                break; // largest first: the rest are smaller still
            AddArcs(g, child, angle, angle + sweep, ring + 1, hue, c, minArc);
            angle += sweep;
        }
    }

    /// <summary>The angle of a point: 0 at the top, growing clockwise, 0..2π.</summary>
    private static double AngleOf(double dx, double dy)
    {
        double a = Math.Atan2(dx, -dy);
        return a < 0 ? a + 2 * Math.PI : a;
    }

    private static void Paint(Picture picture, Geometry g, RenderContext c, CancellationToken cancellationToken)
    {
        double gap = Math.Max(1, c.Scale) * 1.2;
        Rgb middle = c.IsDark ? new Rgb(0.20, 0.20, 0.22) : new Rgb(0.92, 0.925, 0.94);
        int x0 = Math.Max(0, (int)(g.CenterX - g.Radius - 2)), x1 = Math.Min(picture.Width, (int)(g.CenterX + g.Radius + 2));
        int y0 = Math.Max(0, (int)(g.CenterY - g.Radius - 2)), y1 = Math.Min(picture.Height, (int)(g.CenterY + g.Radius + 2));
        for (int py = y0; py < y1; py++)
        {
            if ((py & 31) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            double dy = py + 0.5 - g.CenterY;
            for (int px = x0; px < x1; px++)
            {
                double dx = px + 0.5 - g.CenterX;
                double r = Math.Sqrt(dx * dx + dy * dy);
                if (r > g.Radius + 1)
                    continue;
                if (r < g.Hole)
                {
                    picture.Blend(px, py, middle, Math.Clamp(g.Hole - gap / 2 - r + 0.5, 0, 1));
                    continue;
                }
                int ring = (int)((r - g.Hole) / g.RingWidth) + 1;
                if (ring > g.Rings)
                    continue;
                List<Arc> arcs = g.ByRing[ring];
                if (arcs.Count == 0)
                    continue;
                double a = AngleOf(dx, dy);
                int index = Find(arcs, a);
                if (index < 0)
                    continue;
                Arc arc = arcs[index];
                double inner = g.Hole + g.RingWidth * (ring - 1), outer = inner + g.RingWidth;
                double distance = Math.Min(r - inner, outer - r);
                if (arc.End - arc.Start < 2 * Math.PI - 1e-9)
                    distance = Math.Min(distance, r * Math.Min(a - arc.Start, arc.End - a));
                double cover = Math.Clamp(distance - gap / 2 + 0.5, 0, 1);
                if (cover > 0)
                {
                    // a slight shine toward the outer edge of each ring
                    double t = (r - inner) / g.RingWidth;
                    picture.Blend(px, py, Picture.Shade(arc.Color, 0.96 + 0.08 * t), cover);
                }
            }
        }
    }

    /// <summary>The arc that contains the angle (arcs are in angle order), or -1.</summary>
    private static int Find(List<Arc> arcs, double angle)
    {
        int lo = 0, hi = arcs.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (angle < arcs[mid].Start)
                hi = mid - 1;
            else if (angle >= arcs[mid].End)
                lo = mid + 1;
            else
                return mid;
        }
        return -1;
    }

    private static Node? HitTest(Geometry g, PluginPoint p)
    {
        double dx = p.X - g.CenterX, dy = p.Y - g.CenterY;
        double r = Math.Sqrt(dx * dx + dy * dy);
        if (r < g.Hole)
            return g.Root;
        int ring = (int)((r - g.Hole) / g.RingWidth) + 1;
        if (ring > g.Rings || r > g.Radius)
            return null;
        int index = Find(g.ByRing[ring], AngleOf(dx, dy));
        return index < 0 ? null : g.ByRing[ring][index].Node;
    }

    private static IReadOnlyList<PluginPoint>? Outline(Geometry g, Node node)
    {
        if (node == g.Root)
            return Circle(g, g.Hole);
        if (!g.ByNode.TryGetValue(node, out var found))
            return null;
        double inner = g.Hole + g.RingWidth * (found.Ring - 1), outer = inner + g.RingWidth;
        double start = found.Arc.Start, end = found.Arc.End;
        int steps = Math.Clamp((int)((end - start) * outer / 6), 2, 180);
        var points = new List<PluginPoint>(steps * 2 + 2);
        for (int i = 0; i <= steps; i++)
            points.Add(PointAt(g, start + (end - start) * i / steps, outer));
        for (int i = steps; i >= 0; i--)
            points.Add(PointAt(g, start + (end - start) * i / steps, inner));
        return points;
    }

    private static PluginPoint[] Circle(Geometry g, double radius)
    {
        var points = new PluginPoint[72];
        for (int i = 0; i < points.Length; i++)
            points[i] = PointAt(g, 2 * Math.PI * i / points.Length, radius);
        return points;
    }

    private static PluginPoint PointAt(Geometry g, double angle, double radius) =>
        new(g.CenterX + Math.Sin(angle) * radius, g.CenterY - Math.Cos(angle) * radius);

    /// <summary>Names on the arcs that have room for them (the inner rings first).</summary>
    private static void AddLabels(Frame frame, Geometry g, RenderContext c)
    {
        double s = c.Scale;
        int count = 0;
        for (int ring = 1; ring <= Math.Min(3, g.Rings); ring++)
        {
            double middle = g.Hole + g.RingWidth * (ring - 0.5);
            foreach (Arc arc in g.ByRing[ring])
            {
                // horizontal text: it needs the arc to be tall enough where it crosses the middle of the ring
                if ((arc.End - arc.Start) * middle < 20 * s || g.RingWidth < 44 * s || count >= 80)
                    continue;
                PluginPoint at = PointAt(g, (arc.Start + arc.End) / 2, middle);
                double width = Math.Min(g.RingWidth * 0.92, Math.Max(44 * s, (arc.End - arc.Start) * middle * 0.9));
                frame.Labels.Add(new PluginLabel(at.X - width / 2, at.Y - 9 * s, width, 18 * s, arc.Node.Name,
                    RenderContext.TextOn(arc.Color), FontSize: 11, Bold: ring == 1, Centered: true));
                count++;
            }
        }
    }
}
