// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Updates;

namespace VibeSalamander.Core.Tests;

public sealed class UpdatesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vibe-updates-" + Guid.NewGuid().ToString("N")[..8]);

    public UpdatesTests() => Directory.CreateDirectory(_root);

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

    private const string Release = """
        {
          "tag_name": "v0.8.0",
          "html_url": "https://github.com/BrainCluster/vibe-salamander/releases/tag/v0.8.0",
          "draft": false,
          "body": "**Vibe Salamander 0.8.0**: an installer.\n\n## Download\n\n| File | For |\n|---|---|\n\nUnzip anywhere.\n\n## New\n\n- **Installer** with [updates](https://example.com)\n",
          "assets": [
            { "name": "VibeSalamander-0.8.0-win-x64.zip", "size": 100, "browser_download_url": "https://github.com/x/VibeSalamander-0.8.0-win-x64.zip", "digest": "sha256:ABCDEF" },
            { "name": "VibeSalamander-0.8.0-win-x64-setup.exe", "size": 90, "browser_download_url": "https://github.com/x/VibeSalamander-0.8.0-win-x64-setup.exe" },
            { "name": "VibeSalamander-0.8.0-win-arm64-setup.exe", "size": 80, "browser_download_url": "https://github.com/x/VibeSalamander-0.8.0-win-arm64-setup.exe" },
            { "name": "VibeSalamander-7-Zip-plugin-0.8.0-win-x64.zip", "size": 10, "browser_download_url": "https://github.com/x/plugin.zip" }
          ]
        }
        """;

    [Fact]
    public void A_release_is_read_and_the_right_file_picked()
    {
        ReleaseInfo? release = UpdateCheck.Parse(Release);
        Assert.NotNull(release);
        Assert.Equal("0.8.0", release!.Version);
        Assert.Equal("VibeSalamander-0.8.0-win-x64-setup.exe", UpdateCheck.AssetFor(release, InstallKind.Installed, Architecture.X64)!.Name);
        Assert.Equal("VibeSalamander-0.8.0-win-arm64-setup.exe", UpdateCheck.AssetFor(release, InstallKind.Installed, Architecture.Arm64)!.Name);
        ReleaseAsset zip = UpdateCheck.AssetFor(release, InstallKind.Portable, Architecture.X64)!;
        Assert.Equal("VibeSalamander-0.8.0-win-x64.zip", zip.Name);
        Assert.Equal("abcdef", zip.Sha256);
        Assert.Null(UpdateCheck.AssetFor(release, InstallKind.Portable, Architecture.Arm64));
        Assert.Null(UpdateCheck.AssetFor(release, InstallKind.Scoop, Architecture.X64));
        Assert.Null(UpdateCheck.Parse("{\"message\": \"Not Found\"}"));
        Assert.Null(UpdateCheck.Parse("not json"));
    }

    [Fact]
    public void Versions_compare_as_numbers()
    {
        Assert.True(UpdateCheck.IsNewer("0.8.0", "0.7.0"));
        Assert.True(UpdateCheck.IsNewer("v0.10.0", "0.9.1"));
        Assert.True(UpdateCheck.IsNewer("1.0", "0.9.9"));
        Assert.False(UpdateCheck.IsNewer("0.7.0", "0.7.0"));
        Assert.False(UpdateCheck.IsNewer("0.7.0", "0.7.0+abc123"));
        Assert.False(UpdateCheck.IsNewer("0.6.9", "0.7.0"));
        Assert.False(UpdateCheck.IsNewer("garbage", "0.7.0"));
    }

    [Fact]
    public void Notes_as_plain_text_without_the_download_part()
    {
        string notes = UpdateCheck.PlainNotes(UpdateCheck.Parse(Release)!.Notes);
        Assert.DoesNotContain("Unzip", notes);
        Assert.DoesNotContain("**", notes);
        Assert.DoesNotContain("example.com", notes);
        Assert.Contains("•  Installer with updates", notes);
        Assert.Contains("NEW", notes);
    }

    [Fact]
    public void How_a_copy_was_installed()
    {
        Assert.Equal(InstallKind.Installed, UpdateCheck.KindOf(_root, installed: true, portable: false));
        Assert.Equal(InstallKind.Scoop, UpdateCheck.KindOf(@"C:\Users\me\scoop\apps\vibe-salamander\current", false, true));
        Assert.Equal(InstallKind.Portable, UpdateCheck.KindOf(_root, false, portable: true));
        Assert.Equal(InstallKind.Development, UpdateCheck.KindOf(_root, false, false));
        // an older zip (no portable.flag): the starter and lib
        File.WriteAllText(Path.Combine(_root, "VibeSalamander.exe"), "starter");
        Directory.CreateDirectory(Path.Combine(_root, "lib"));
        Assert.Equal(InstallKind.Portable, UpdateCheck.KindOf(_root, false, false));
    }

    [Fact]
    public void The_main_folder_is_above_lib()
    {
        string lib = Path.Combine(_root, "lib");
        Directory.CreateDirectory(lib);
        Assert.Equal(lib, AppPaths.FindMainFolder(lib + Path.DirectorySeparatorChar)); // no starter: the folder itself
        File.WriteAllText(Path.Combine(_root, "VibeSalamander.exe"), "starter");
        Assert.Equal(_root, AppPaths.FindMainFolder(lib + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Settings_move_into_a_new_portable_folder_once()
    {
        string roaming = Path.Combine(_root, "roaming"), data = Path.Combine(_root, "Data");
        Directory.CreateDirectory(Path.Combine(roaming, "Plugins", "7-Zip"));
        File.WriteAllText(Path.Combine(roaming, "settings.json"), "{\"a\":1}");
        File.WriteAllText(Path.Combine(roaming, "Plugins", "7-Zip", "plugin.dll"), "x");
        Directory.CreateDirectory(Path.Combine(roaming, "Logs"));
        File.WriteAllText(Path.Combine(roaming, "Logs", "old.log"), "log");
        Assert.True(AppPaths.CopySettings(roaming, data));
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(data, "settings.json")));
        Assert.True(File.Exists(Path.Combine(data, "Plugins", "7-Zip", "plugin.dll")));
        Assert.False(File.Exists(Path.Combine(data, "Logs", "old.log")));
        File.WriteAllText(Path.Combine(roaming, "settings.json"), "{\"a\":2}");
        Assert.False(AppPaths.CopySettings(roaming, data)); // Data has its own settings now
        Assert.Equal("{\"a\":1}", File.ReadAllText(Path.Combine(data, "settings.json")));
    }
}
