// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;

namespace VibeSalamander.Core.Settings;

/// <summary>
/// A Visual Studio Code color theme (its .json, from an extension's "themes" folder) as the panels' custom colors:
/// the side bar's background and text, the list's selection for the cursor bar, the terminal's blue for folders.
/// </summary>
public static class VsCodeThemes
{
    /// <summary>Fills the custom colors from the theme and selects the Custom scheme; false (with why) when it isn't one.</summary>
    public static bool TryImport(string json, ColorOptions colors, out string? problem)
    {
        problem = null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            problem = ex.Message;
            return false;
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("colors", out JsonElement map) || map.ValueKind != JsonValueKind.Object)
            {
                problem = Localization.L.T("The file has no \"colors\": it isn't a Visual Studio Code color theme.");
                return false;
            }
            string? Get(params string[] keys)
            {
                foreach (string key in keys)
                    if (map.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String && Color(value.GetString()) is { } color)
                        return color;
                return null;
            }
            string? background = Get("sideBar.background", "editor.background");
            string? text = Get("sideBar.foreground", "foreground", "editor.foreground");
            if (background == null || text == null)
            {
                problem = Localization.L.T("The theme doesn't say its background and text colors.");
                return false;
            }
            colors.PanelBackground = background;
            colors.Text = text;
            colors.SecondaryText = Get("descriptionForeground", "editorLineNumber.foreground", "sideBarSectionHeader.foreground") ?? text;
            colors.Selected = Get("gitDecoration.modifiedResourceForeground", "editorWarning.foreground", "terminal.ansiYellow") ?? text;
            colors.Folder = Get("symbolIcon.folderForeground", "terminal.ansiBlue", "textLink.foreground") ?? text;
            colors.DirectoryText = Get("terminal.ansiBlue", "textLink.foreground") ?? text;
            colors.FocusBackground = Get("list.activeSelectionBackground", "list.focusBackground", "editor.selectionBackground") ?? background;
            colors.FocusText = Get("list.activeSelectionForeground", "list.focusForeground") ?? text;
            colors.Scheme = "Custom";
            return true;
        }
    }

    /// <summary>"#RGB", "#RGBA", "#RRGGBB" or "#RRGGBBAA" (VS Code puts the alpha last) → "#RRGGBB"; null otherwise.</summary>
    public static string? Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] != '#')
            return null;
        string hex = value[1..];
        if (hex.Length is 3 or 4)
            hex = string.Concat(hex[..3].Select(c => new string(c, 2)));
        if (hex.Length == 8)
            hex = hex[..6];
        if (hex.Length != 6 || !hex.All(Uri.IsHexDigit))
            return null;
        return "#" + hex.ToUpperInvariant();
    }
}
