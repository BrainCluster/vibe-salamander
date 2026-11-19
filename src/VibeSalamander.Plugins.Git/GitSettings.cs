// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace VibeSalamander.Plugins.Git;

/// <summary>The plugin's settings (settings.json in its data folder).</summary>
internal sealed class GitSettings
{
    /// <summary>git.exe's full path; empty: the first on the PATH, else Git for Windows' usual places.</summary>
    public string GitPath { get; set; } = string.Empty;

    /// <summary>Where Clone puts repositories by default (empty: the active panel's folder).</summary>
    public string CloneFolder { get; set; } = string.Empty;

    public static GitSettings Load(string file)
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<GitSettings>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save(string file)
    {
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not saved: the settings hold until the program ends
        }
    }
}
