// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Panels;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.Core.Tests;

/// <summary>A damaged or foreign settings file must never cost the user all their settings, nor stop the program.</summary>
public sealed class SettingsRobustnessTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("vibe-settings").FullName;
    private string File_ => Path.Combine(_folder, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string[] Kept() => Directory.GetFiles(_folder, "settings.broken-*.json");

    [Fact]
    public void Round_trip_keeps_the_settings_and_leaves_no_temporary_file()
    {
        var settings = new AppSettings { ShowHidden = true, ViewStyle = ViewStyle.Modern };
        settings.Left.ViewMode = ViewMode.Tiles;
        settings.HotPaths[3] = @"D:\Work";
        SettingsStore.Save(settings, File_);

        AppSettings loaded = SettingsStore.Load(File_, out string? problem);

        Assert.Null(problem);
        Assert.True(loaded.ShowHidden);
        Assert.Equal(ViewStyle.Modern, loaded.ViewStyle);
        Assert.Equal(ViewMode.Tiles, loaded.Left.ViewMode);
        Assert.Equal(@"D:\Work", loaded.HotPaths[3]);
        Assert.Equal(new[] { File_ }, Directory.GetFiles(_folder));
    }

    [Fact]
    public void A_value_it_doesnt_know_goes_back_to_its_default_and_the_rest_stays()
    {
        // a newer version's view, a typing mistake in a number
        File.WriteAllText(File_, """
            {
              "ShowHidden": true,
              "Left": { "Path": "D:\\Photos", "ViewMode": "Gallery" },
              "SplitRatio": "half",
              "HotPaths": ["", "C:\\Temp", "", "", "", "", "", "", "", ""]
            }
            """);

        AppSettings loaded = SettingsStore.Load(File_, out string? problem);

        Assert.True(loaded.ShowHidden);
        Assert.Equal(@"D:\Photos", loaded.Left.Path);
        Assert.Equal(new PanelSettings().ViewMode, loaded.Left.ViewMode);
        Assert.Equal(new AppSettings().SplitRatio, loaded.SplitRatio);
        Assert.Equal(@"C:\Temp", loaded.HotPaths[1]);
        Assert.NotNull(problem);
        Assert.Contains("Left.ViewMode", problem);
        Assert.Contains("SplitRatio", problem);
        Assert.Single(Kept()); // the original is kept beside it
    }

    [Fact]
    public void A_file_that_isnt_settings_gives_the_defaults_and_is_kept_once()
    {
        File.WriteAllText(File_, "this is not JSON {");

        AppSettings first = SettingsStore.Load(File_, out string? problem);
        SettingsStore.Load(File_, out _); // the program reads it twice at start: one copy is enough

        Assert.NotNull(problem);
        Assert.False(first.ShowHidden);
        Assert.Equal(10, first.HotPaths.Count);
        string kept = Assert.Single(Kept());
        Assert.Equal("this is not JSON {", File.ReadAllText(kept));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    public void Empty_or_odd_files_give_the_defaults(string content)
    {
        File.WriteAllText(File_, content);
        AppSettings loaded = SettingsStore.Load(File_, out string? problem);
        Assert.NotNull(problem);
        Assert.NotNull(loaded.Left);
        Assert.Equal(10, loaded.HotPaths.Count);
    }

    [Fact]
    public void Nulls_get_their_defaults()
    {
        File.WriteAllText(File_, """{ "ShowHidden": true, "Left": null, "UserMenu": null, "HotPaths": null, "Panels": null }""");

        AppSettings loaded = SettingsStore.Load(File_, out _);

        Assert.True(loaded.ShowHidden);
        Assert.NotNull(loaded.Left);
        Assert.NotNull(loaded.Panels);
        Assert.NotEmpty(loaded.UserMenu); // the default user menu, not an empty one
        Assert.Equal(10, loaded.HotPaths.Count);
    }

    [Fact]
    public void Missing_hot_paths_are_filled_up_to_ten()
    {
        File.WriteAllText(File_, """{ "HotPaths": ["", "C:\\One", null] }""");
        AppSettings loaded = SettingsStore.Load(File_, out _);
        Assert.Equal(10, loaded.HotPaths.Count);
        Assert.Equal(@"C:\One", loaded.HotPaths[1]);
        Assert.Equal(string.Empty, loaded.HotPaths[2]);
    }

    [Fact]
    public void Comments_and_trailing_commas_written_by_hand_are_accepted()
    {
        File.WriteAllText(File_, """
            {
              // shown files
              "ShowHidden": true,
            }
            """);
        AppSettings loaded = SettingsStore.Load(File_, out string? problem);
        Assert.Null(problem);
        Assert.True(loaded.ShowHidden);
    }

    [Fact]
    public void A_locked_file_isnt_overwritten_with_the_defaults()
    {
        var original = new AppSettings { ShowHidden = true };
        SettingsStore.Save(original, File_);
        string before = File.ReadAllText(File_);

        AppSettings loaded;
        string? problem;
        using (new FileStream(File_, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            loaded = SettingsStore.Load(File_, out problem);
        SettingsStore.Save(loaded, File_);

        Assert.NotNull(problem);
        Assert.True(loaded.DoNotSave);
        Assert.Equal(before, File.ReadAllText(File_));
    }
}
