// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.DiskMap;

/// <summary>
/// The original Disk Map view (Classic), modernised: a squarified cushion treemap (van Wijk and van de Wetering).
/// Every file is a tile as large as the file; each directory level adds a ridge to a shaded surface, so
/// directories show as bumps of tiles. Files are colored by what they are.
/// </summary>
internal sealed class CushionView : IMapView
{
    public string Name => "Classic";

    private const double StartHeight = 0.38;
    private const double HeightFactor = 0.88;
    private const double Ambient = 0.16;
    private static readonly double LightX, LightY, LightZ;

    static CushionView()
    {
        // light from the upper left
        double length = Math.Sqrt(1 + 1 + 100);
        LightX = -1 / length;
        LightY = -1 / length;
        LightZ = 10 / length;
    }

    private sealed class State
    {
        public required Picture Picture;
        public required RectIndex Index;
        public required RenderContext Context;
        public required CancellationToken Cancellation;
        public readonly List<(double Area, PluginLabel Label)> Labels = new();
        public int Count;
    }

    public Frame Render(Node root, RenderContext context, CancellationToken cancellationToken)
    {
        var picture = new Picture(context.Width, context.Height);
        var frame = new Frame(picture);
        var state = new State { Picture = picture, Index = new RectIndex(), Context = context, Cancellation = cancellationToken };
        Draw(state, root, 0, 0, picture.Width, picture.Height, new double[4], StartHeight, 0, double.NegativeInfinity);
        state.Index.AttachTo(frame);
        foreach (var (_, label) in state.Labels.OrderByDescending(l => l.Area).Take(48))
            frame.Labels.Add(label);
        AddKindLegend(frame, root, context);
        return frame;
    }

    private static void Draw(State state, Node node, double x, double y, double w, double h, double[] parentSurface,
        double height, int level, double parentLabelBottom)
    {
        if (w < 0.5 || h < 0.5)
            return;
        if (++state.Count % 4096 == 0)
            state.Cancellation.ThrowIfCancellationRequested();
        state.Index.Add(node, x, y, w, h);
        var surface = (double[])parentSurface.Clone();
        AddRidge(surface, x, y, w, h, height);

        bool leaf = !node.IsDirectory || node.Children.Length == 0 || node.Size <= 0 || w * h < 12;
        if (leaf)
        {
            Cushion(state, x, y, w, h, surface, ColorOf(node), node.IsDirectory && node.Size <= 0);
            return;
        }

        // directories with many tiny files: their tiles round to nothing, so the directory's own cushion shows there
        Node smallest = node.Children[^1];
        if (smallest.Size * (w * h / node.Size) < 2)
            Cushion(state, x, y, w, h, surface, ColorOf(node), empty: false);

        double labelBottom = parentLabelBottom;
        RenderContext c = state.Context;
        if (level >= 1 && level <= 2 && w >= 84 * c.Scale && h >= 26 * c.Scale && y >= parentLabelBottom)
        {
            double labelHeight = 20 * c.Scale;
            state.Labels.Add((w * h, new PluginLabel(x + 4 * c.Scale, y + 4 * c.Scale, w - 8 * c.Scale, labelHeight,
                $"{node.Name}  {Layout.FormatSize(node.Size)}", 0xFFFFFFFF, Background: 0x99141418, FontSize: 12, Bold: level == 1)));
            labelBottom = y + 4 * c.Scale + labelHeight;
        }
        Layout.Squarify(node.Children, x, y, w, h,
            (child, cx, cy, cw, ch) => Draw(state, child, cx, cy, cw, ch, surface, height * HeightFactor, level + 1, labelBottom));
    }

    private static Rgb ColorOf(Node node) =>
        Rgb.From(FileKinds.Color(node.IsDirectory ? Layout.DominantKind(node) : node.Kind));

    /// <summary>A ridge over the rectangle: the surface rises toward its middle (parabolas in x and y).</summary>
    private static void AddRidge(double[] s, double x, double y, double w, double h, double height)
    {
        double h4 = 4 * height;
        double wf = h4 / w;
        s[2] += wf * (x + x + w);
        s[0] -= wf;
        double hf = h4 / h;
        s[3] += hf * (y + y + h);
        s[1] -= hf;
    }

    private static void Cushion(State state, double x, double y, double w, double h, double[] s, Rgb color, bool empty)
    {
        Picture picture = state.Picture;
        int x0 = Math.Max(0, (int)Math.Round(x)), x1 = Math.Min(picture.Width, (int)Math.Round(x + w));
        int y0 = Math.Max(0, (int)Math.Round(y)), y1 = Math.Min(picture.Height, (int)Math.Round(y + h));
        if (empty)
            color = color.Mix(state.Context.Background, 0.6);
        double brightness = state.Context.IsDark ? 1.12 : 1.22;
        double diffuse = 1 - Ambient;
        for (int py = y0; py < y1; py++)
        {
            double ny = -(2 * s[1] * (py + 0.5) + s[3]);
            for (int px = x0; px < x1; px++)
            {
                double nx = -(2 * s[0] * (px + 0.5) + s[2]);
                double cos = (nx * LightX + ny * LightY + LightZ) / Math.Sqrt(nx * nx + ny * ny + 1);
                if (cos > 1)
                    cos = 1;
                double light = diffuse * cos;
                if (light < 0)
                    light = 0;
                light = (light + Ambient) * brightness;
                picture.Set(px, py, light <= 1 ? color.Scale(light) : color.Mix(Rgb.White, Math.Min(0.6, (light - 1) * 0.9)));
            }
        }
    }

    /// <summary>The legend of the views that color by file kind: the kinds there, largest first, with their sizes.</summary>
    public static void AddKindLegend(Frame frame, Node root, RenderContext context)
    {
        var totals = new long[Enum.GetValues<FileKind>().Length];
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            Node n = stack.Pop();
            if (!n.IsDirectory)
            {
                totals[(int)n.Kind] += n.Size;
                continue;
            }
            foreach (Node child in n.Children)
                stack.Push(child);
        }
        foreach (FileKind kind in FileKinds.All.Where(k => totals[(int)k] > 0).OrderByDescending(k => totals[(int)k]))
            frame.Legend.Add(new PluginLegendItem(0xFF000000 | FileKinds.Color(kind),
                $"{context.T(FileKinds.Name(kind))}  {Layout.FormatSize(totals[(int)kind])}"));
    }
}
