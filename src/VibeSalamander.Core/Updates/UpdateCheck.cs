// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VibeSalamander.Core.Updates;

/// <summary>A file of a release: its name, where to download it, its size and (when GitHub gives it) its SHA-256.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>A release on GitHub: its version ("0.8.0"), its notes (Markdown), its page and its files.</summary>
public sealed record ReleaseInfo(string Version, string Notes, string PageUrl, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>How this copy came to the computer, which decides how it's updated.</summary>
public enum InstallKind
{
    /// <summary>With the installer: the new installer runs (silently) and starts the program again.</summary>
    Installed,
    /// <summary>A zip (portable): the new zip's files replace these, the Data folder stays.</summary>
    Portable,
    /// <summary>Through Scoop: Scoop updates it.</summary>
    Scoop,
    /// <summary>A build from the source: only the release page.</summary>
    Development,
}

/// <summary>The update check: the newest release on GitHub against this version.</summary>
public static partial class UpdateCheck
{
    public const string Repository = "BrainCluster/vibe-salamander";
    public const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    /// <summary>The newest release from the GitHub API's answer, or null when it isn't one.</summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out JsonElement tag) || tag.GetString() is not { } name)
                return null;
            if (root.TryGetProperty("draft", out JsonElement draft) && draft.ValueKind == JsonValueKind.True)
                return null;
            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in list.EnumerateArray())
                {
                    string? file = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                    string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                    if (file == null || url == null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        continue;
                    long size = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long bytes) ? bytes : 0;
                    // "digest": "sha256:ab12..." (GitHub gives it for newer uploads)
                    string? digest = asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null;
                    string? sha = digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null;
                    assets.Add(new ReleaseAsset(file, url, size, sha));
                }
            }
            string notes = root.TryGetProperty("body", out JsonElement body) ? body.GetString() ?? string.Empty : string.Empty;
            string page = root.TryGetProperty("html_url", out JsonElement html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
            return new ReleaseInfo(name.TrimStart('v', 'V'), notes, page, assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"0.8.0" is newer than "0.7.0" (and "0.10.0" than "0.9.1"); anything after "-" or "+" is ignored.</summary>
    public static bool IsNewer(string candidate, string current) =>
        ParseVersion(candidate) is { } a && ParseVersion(current) is { } b && a > b;

    private static Version? ParseVersion(string text)
    {
        string core = text.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        string[] parts = core.Split('.');
        while (parts.Length < 3)
            parts = parts.Append("0").ToArray();
        return Version.TryParse(string.Join('.', parts.Take(4)), out Version? version) ? version : null;
    }

    /// <summary>
    /// The file to update with: the installer ("...-win-x64-setup.exe") for an installed copy, the zip
    /// ("...-win-x64.zip") for a portable one; for this processor (x64 or ARM64).
    /// </summary>
    public static ReleaseAsset? AssetFor(ReleaseInfo release, InstallKind kind, Architecture architecture)
    {
        string rid = architecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        string ending = kind switch
        {
            InstallKind.Installed => $"-{rid}-setup.exe",
            InstallKind.Portable => $"-{rid}.zip",
            _ => "\0",
        };
        return release.Assets.FirstOrDefault(a => a.Name.StartsWith("VibeSalamander-", StringComparison.OrdinalIgnoreCase) &&
                                                  !a.Name.Contains("plugin", StringComparison.OrdinalIgnoreCase) &&
                                                  a.Name.EndsWith(ending, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>How the copy in <paramref name="mainFolder"/> was installed.</summary>
    public static InstallKind KindOf(string mainFolder, bool installed, bool portable)
    {
        if (installed)
            return InstallKind.Installed;
        if (mainFolder.Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase))
            return InstallKind.Scoop;
        // the zip's layout: the starter and lib (an older zip without portable.flag gets it with the update)
        if (portable || (File.Exists(Path.Combine(mainFolder, "VibeSalamander.exe")) && Directory.Exists(Path.Combine(mainFolder, "lib"))))
            return InstallKind.Portable;
        return InstallKind.Development;
    }

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();

    /// <summary>Release notes in Markdown as plain text for a dialog: no stars, link targets, table bars or "##".</summary>
    public static string PlainNotes(string markdown)
    {
        var lines = new List<string>();
        bool skipping = false;
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            // the "## Download" part is about the files, which the update picks by itself
            if (line.StartsWith("## ", StringComparison.Ordinal))
                skipping = line[3..].Trim().StartsWith("Download", StringComparison.OrdinalIgnoreCase);
            if (skipping)
                continue;
            if (line.StartsWith('|') || line.StartsWith("```", StringComparison.Ordinal))
                continue; // tables (the downloads) and code fences
            line = Link().Replace(line, "$1").Replace("**", string.Empty).Replace("`", string.Empty);
            if (line.StartsWith('#'))
                line = line.TrimStart('#').Trim().ToUpperInvariant();
            else if (line.StartsWith("- ", StringComparison.Ordinal))
                line = "•  " + line[2..];
            lines.Add(line);
        }
        return string.Join('\n', lines).Trim();
    }
}
