// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Panels;

namespace VibeSalamander.Core.Tests;

/// <summary>A plugin that needs a newer Vibe Salamander is refused (IVibePlugin.MinimumHostVersion).</summary>
public sealed class PluginVersionsTests
{
    [Theory]
    [InlineData("1.0.0.0", "1.0", true)]
    [InlineData("1.0", "1.0.0", true)]
    [InlineData("1.2.3", "1.0", true)]
    [InlineData("1.0.0.7", "1.0.1", false)]
    [InlineData("0.9.5", "1.0", false)]
    [InlineData("2.0", "1.9.9", true)]
    public void A_host_meets_a_plugins_minimum(string host, string needs, bool met) =>
        Assert.Equal(met, PluginVersions.Satisfies(Version.Parse(host), Version.Parse(needs)));

    [Theory]
    [InlineData("1.0", "1.0")]
    [InlineData("1.0.0.0", "1.0")]
    [InlineData("1.0.3", "1.0.3")]
    [InlineData("0.9.0.0", "0.9")]
    public void Versions_read_as_people_write_them(string version, string text) =>
        Assert.Equal(text, PluginVersions.Text(Version.Parse(version)));
}
