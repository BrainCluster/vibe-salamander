// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Tests;

public class DirectorySizeTests
{
    [Fact]
    public async Task SumsFilesAndDirectoriesRecursively()
    {
        string root = Path.Combine(Path.GetTempPath(), "vs-size-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "a", "b"));
            await File.WriteAllBytesAsync(Path.Combine(root, "one.bin"), new byte[100]);
            await File.WriteAllBytesAsync(Path.Combine(root, "a", "two.bin"), new byte[250]);
            await File.WriteAllBytesAsync(Path.Combine(root, "a", "b", "three.bin"), new byte[1]);

            OccupiedSpace space = await DirectorySize.CalculateAsync(root);

            Assert.Equal(351, space.Bytes);
            Assert.Equal(3, space.Files);
            Assert.Equal(2, space.Directories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class Phase3KeyMapTests
{
    [Fact]
    public void HotPathAndViewKeys()
    {
        KeyMap map = KeyMap.CreateSalamanderDefaults();
        Assert.Equal(CommandId.GoToHotPath3, map.Resolve(VK.D0 + 3, KeyModifiers.Ctrl));
        Assert.Equal(CommandId.SetHotPath1, map.Resolve(VK.D0 + 1, KeyModifiers.Ctrl | KeyModifiers.Shift));
        Assert.Equal(CommandId.None, map.Resolve(VK.D0, KeyModifiers.Ctrl)); // Windows takes Ctrl+Shift+0: no hot path 0
        Assert.Equal(CommandId.ViewThumbnails, map.Resolve(VK.D4, KeyModifiers.Alt));
        Assert.Equal(CommandId.DriveInformation, map.Resolve(VK.F1, KeyModifiers.Ctrl));
        Assert.Equal("Ctrl+7", map.GetShortcutText(CommandId.GoToHotPath7));
    }
}
