// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Plugins;
using VibeSalamander.Plugins.DiskMap;

namespace VibeSalamander.Core.Tests;

public sealed class DiskMapPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vibe-diskmap-" + Guid.NewGuid().ToString("N"));

    public DiskMapPluginTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "photos", "2025"));
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(Path.Combine(_root, "empty"));
        File.WriteAllBytes(Path.Combine(_root, "photos", "2025", "a.jpg"), new byte[6000]);
        File.WriteAllBytes(Path.Combine(_root, "photos", "b.png"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(_root, "docs", "c.pdf"), new byte[2000]);
        File.WriteAllBytes(Path.Combine(_root, "readme.txt"), new byte[1000]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Node Scan() => new Scanner().Scan(_root, CancellationToken.None);

    [Fact]
    public void Scan_SumsSizesAndSortsLargestFirst()
    {
        Node tree = Scan();
        Assert.Equal(12000, tree.Size);
        Assert.Equal(4, tree.Files);
        Assert.Equal(4, tree.Directories); // photos, photos\2025, docs, empty
        Assert.Equal(new[] { "photos", "docs", "readme.txt", "empty" }, tree.Children.Select(c => c.Name));
        Node photos = tree.Children[0];
        Assert.Equal(9000, photos.Size);
        Assert.Equal(Path.Combine(_root, "photos", "2025", "a.jpg"), photos.Children[0].Children[0].FullPath);
        Assert.Equal(FileKind.Images, photos.Children[0].Children[0].Kind);
    }

    [Fact]
    public void Squarify_FillsTheRectangleInProportion()
    {
        Node tree = Scan();
        double area = 0;
        var tiles = new Dictionary<string, double>();
        Layout.Squarify(tree.Children, 10, 20, 300, 200, (node, x, y, w, h) =>
        {
            Assert.True(x >= 10 - 1e-6 && y >= 20 - 1e-6 && x + w <= 310 + 1e-6 && y + h <= 220 + 1e-6);
            area += w * h;
            tiles[node.Name] = w * h;
        });
        Assert.Equal(300 * 200, area, 3);
        Assert.Equal(300 * 200 * 9000 / 12000.0, tiles["photos"], 3);
        Assert.False(tiles.ContainsKey("empty")); // nothing to show
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Views_DrawAndFindTheItems(int index)
    {
        IMapView[] views = { new CushionView(), new BlocksView(), new SunburstView(), new ColumnsView() };
        Node tree = Scan();
        var context = new RenderContext { Width = 400, Height = 300, Scale = 1, IsDark = index % 2 == 0 };
        Frame frame = views[index].Render(tree, context, CancellationToken.None);
        Assert.Equal(400 * 300 * 4, frame.Picture.Pixels.Length);
        Assert.Contains(frame.Picture.Pixels, b => b != 0); // something was drawn

        // every point of the picture finds an item of the tree (or nothing), and that item has an outline
        int found = 0;
        for (int y = 5; y < 300; y += 10)
        {
            for (int x = 5; x < 400; x += 10)
            {
                if (frame.HitTest(new PluginPoint(x, y)) is not { } node)
                    continue;
                Assert.True(node.IsWithin(tree));
                Assert.NotNull(frame.Outline(node));
                found++;
            }
        }
        Assert.True(found > 100);
        Assert.NotEmpty(frame.Legend);
    }

    [Fact]
    public void Views_StopWhenCancelled()
    {
        Node tree = Scan();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var context = new RenderContext { Width = 800, Height = 600 };
        Assert.ThrowsAny<OperationCanceledException>(() => new SunburstView().Render(tree, context, cancel.Token));
    }

    [Fact]
    public void FileKinds_ByExtension()
    {
        Assert.Equal(FileKind.Video, FileKinds.Of("movie.MKV"));
        Assert.Equal(FileKind.Programs, FileKinds.Of("app.exe"));
        Assert.Equal(FileKind.Data, FileKinds.Of("pagefile.sys"));
        Assert.Equal(FileKind.Other, FileKinds.Of("Makefile"));
        Assert.Equal(FileKind.Other, FileKinds.Of(".gitignore"));
    }

    [Fact]
    public void FormatSize_UsesBinaryUnits()
    {
        Assert.Equal("512 B", Layout.FormatSize(512));
        Assert.Equal("1 KB", Layout.FormatSize(1024));
        Assert.EndsWith(" GB", Layout.FormatSize(5L * 1024 * 1024 * 1024));
    }
}
