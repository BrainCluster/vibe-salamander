// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Panels;

/// <summary>Comparing versions for plugins: "needs 1.0" is met by 1.0, 1.0.0.0 and 1.2.3, not by 0.9.5.</summary>
public static class PluginVersions
{
    public static bool Satisfies(Version host, Version needs) => Normal(host).CompareTo(Normal(needs)) >= 0;

    /// <summary>"1.0", "1.0.3" (a version's missing parts and a zero build left out).</summary>
    public static string Text(Version version) =>
        version.Build > 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}";

    private static Version Normal(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
}
