// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Services;

namespace VibeSalamander.Views;

/// <summary>What Help → About and the About page of the configuration show: version, authors, license, credits.</summary>
public static class AboutContent
{
    public static StackPanel Build()
    {
        Version? version = typeof(AboutContent).Assembly.GetName().Version;
        var body = new StackPanel { Spacing = 10, MaxWidth = 460 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        header.Children.Add(new Image
        {
            Width = 56,
            Height = 56,
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.png"))),
        });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock { Text = "Vibe Salamander", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Tag = "NoLoc" });
        name.Children.Add(new TextBlock { Text = Loc.F("Version {0}", version == null ? "0.1" : $"{version.Major}.{version.Minor}.{version.Build}"), Opacity = 0.7, Tag = "NoLoc" });
        header.Children.Add(name);
        body.Children.Add(header);

        TextBlock Para(string text, double opacity = 1) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = opacity, Tag = "NoLoc" };
        HyperlinkButton Link(string text, string url) =>
            new() { Content = text, NavigateUri = new Uri(url), Padding = new Thickness(0), Tag = "NoLoc" };

        body.Children.Add(Para(Loc.T("A modern spiritual successor to Altap / Open Salamander: dual-pane, keyboard-first, fast and precise, built with C# and WinUI 3.")));
        body.Children.Add(Para(Loc.T("Written by Claude (Anthropic's AI model), with BrainCluster as its guide.")));
        body.Children.Add(Para("Copyright © 2026 Vibe Salamander Authors", 0.8));
        body.Children.Add(Para(Loc.T("This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It comes with ABSOLUTELY NO WARRANTY."), 0.8));
        body.Children.Add(Link(Loc.T("GNU General Public License v3"), "https://www.gnu.org/licenses/gpl-3.0.html"));

        // Open Salamander: the program this one follows, and the source of the icon
        body.Children.Add(new TextBlock { Text = Loc.T("Credits"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), Tag = "NoLoc" });
        body.Children.Add(Para(Loc.T("Vibe Salamander is inspired by Servant / Altap / Open Salamander, created by Petr Šolín and developed by Altap, Fine and the Open Salamander authors. The application icon is based on the Open Salamander icon (© 2023 Open Salamander Authors, GPL-2.0-or-later), recolored.")));
        body.Children.Add(Para("Open Salamander: David Andrš, Lukáš Cerman, Jakub Červený, Tomáš Jelínek, Milan Kaše, Tomáš Kopal, " +
                               "Jan Patera, Martin Přikryl, Juraj Rojko, Jan Ryšavý, Petr Šolín", 0.8));
        body.Children.Add(Link("Open Salamander", "https://github.com/OpenSalamander/salamander"));
        body.Children.Add(Para(Loc.T("Built with .NET, the Windows App SDK (WinUI 3) and SharpCompress (MIT licenses). See LICENSE.txt and CREDITS.md next to the program."), 0.8));
        body.Children.Add(Para(Loc.T("Settings:") + " " + Core.Settings.SettingsStore.FilePath, 0.6));
        body.Children.Add(Link("Vibe Salamander", "https://github.com/BrainCluster/vibe-salamander"));

        return body;
    }
}
