// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace VibeSalamander.Services;

/// <summary>
/// The user interface language (Configuration → General → Language). Translations are JSON files
/// "Strings\&lt;code&gt;.json" next to the app: { "English text": "translation" }. Texts with
/// placeholders ("Copy {0} to:") also translate texts made from them ("Copy "a.txt" to:").
/// Texts are translated where they are made (T / F) and, for XAML and dialogs, by walking the
/// element tree (Translate) - so most of the UI needs no code for it.
/// </summary>
public static class Loc
{
    /// <summary>Code and native name of every language with a translation ("" = the Windows language).</summary>
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"),
        ("hr", "Hrvatski"),
        ("cs", "Čeština"),
        ("de", "Deutsch"),
        ("es", "Español"),
        ("fr", "Français"),
        ("it", "Italiano"),
        ("nl", "Nederlands"),
        ("pl", "Polski"),
        ("pt-BR", "Português (Brasil)"),
        ("ru", "Русский"),
        ("uk", "Українська"),
    };

    private static Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private static List<Pattern> _patterns = new();
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    private sealed record Pattern(Regex Regex, string Translation, int Holes);

    /// <summary>The language in use ("en", "hr", ...).</summary>
    public static string Current { get; private set; } = "en";

    public static bool IsEnglish => Current == "en";

    /// <summary>Folder with the translation files.</summary>
    public static string StringsFolder => Path.Combine(AppContext.BaseDirectory, "Strings");

    /// <summary>Loads a language; "" = the Windows display language when there is a translation for it.</summary>
    public static void Load(string? setting)
    {
        string code = string.IsNullOrWhiteSpace(setting) ? FromWindows() : setting.Trim();
        _exact = new Dictionary<string, string>(StringComparer.Ordinal);
        _patterns = new List<Pattern>();
        lock (CacheLock)
            Cache.Clear();
        Current = "en";
        if (code == "en")
            return;
        string file = Path.Combine(StringsFolder, code + ".json");
        try
        {
            if (!File.Exists(file))
                return;
            var table = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file));
            if (table == null)
                return;
            foreach (var (english, translated) in table)
            {
                if (string.IsNullOrEmpty(english) || string.IsNullOrEmpty(translated))
                    continue;
                _exact[english] = translated;
                // texts made from a format ("Copy {0} to:") are recognized only when the fixed part is
                // distinctive enough: "{0} file" would also match a file name ending in " file"
                if (Placeholder.IsMatch(english) && Placeholder.Replace(english, string.Empty).Count(char.IsLetter) >= 6)
                    _patterns.Add(ToPattern(english, translated));
            }
            // the most specific (longest fixed text) first
            _patterns.Sort((a, b) => b.Regex.ToString().Length.CompareTo(a.Regex.ToString().Length));
            Current = code;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // a broken file: English
        }
        Core.Localization.L.Translate = T;
    }

    /// <summary>The first language of Windows that has a translation, else English.</summary>
    private static string FromWindows()
    {
        CultureInfo ui = CultureInfo.CurrentUICulture;
        foreach (string candidate in new[] { ui.Name, ui.TwoLetterISOLanguageName })
            foreach (var (code, _) in Languages)
                if (string.Equals(code, candidate, StringComparison.OrdinalIgnoreCase))
                    return code;
        if (ui.TwoLetterISOLanguageName == "pt")
            return "pt-BR";
        return "en";
    }

    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    private static Pattern ToPattern(string english, string translated)
    {
        var sb = new System.Text.StringBuilder("^");
        int last = 0, holes = 0;
        foreach (Match m in Placeholder.Matches(english))
        {
            sb.Append(Regex.Escape(english[last..m.Index]));
            sb.Append("(?<p").Append(m.Groups[1].Value).Append(">.+?)");
            last = m.Index + m.Length;
            holes = Math.Max(holes, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) + 1);
        }
        sb.Append(Regex.Escape(english[last..])).Append('$');
        return new Pattern(new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20)), translated, holes);
    }

    /// <summary>The text in the current language (unchanged when there is no translation).</summary>
    public static string T(string? english) => Translate(english, depth: 0);

    /// <summary>A translated format: F("Copy {0} to:", name).</summary>
    public static string F(string englishFormat, params object?[] args)
    {
        string format = T(englishFormat);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return string.Format(CultureInfo.CurrentCulture, englishFormat, args); // a broken translation
        }
    }

    private static string Translate(string? text, int depth)
    {
        if (string.IsNullOrEmpty(text) || Current == "en")
            return text ?? string.Empty;
        if (_exact.TryGetValue(text, out string? exact))
            return exact;
        // "Configuration..." with only "Configuration" in the table: the same text with the dots
        if (text.EndsWith("...", StringComparison.Ordinal) && text.Length > 3 && _exact.TryGetValue(text[..^3], out string? baseText))
            return baseText + "...";
        if (depth > 1 || _patterns.Count == 0 || text.Length > 2000)
            return text;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(text, out string? cached))
                return cached;
        }
        string result = text;
        foreach (Pattern pattern in _patterns)
        {
            Match m;
            try
            {
                m = pattern.Regex.Match(text);
            }
            catch (RegexMatchTimeoutException)
            {
                continue; // an unusual text: not worth the time
            }
            if (!m.Success)
                continue;
            result = Placeholder.Replace(pattern.Translation, ph =>
            {
                Group g = m.Groups["p" + ph.Groups[1].Value];
                return g.Success ? Translate(g.Value, depth + 1) : ph.Value;
            });
            break;
        }
        lock (CacheLock)
        {
            if (Cache.Count > 20000)
                Cache.Clear();
            Cache[text] = result;
        }
        return result;
    }

    // ---- element trees ------------------------------------------------------------------------

    /// <summary>
    /// Translates the fixed texts of a tree of elements: text blocks, buttons, headers, placeholders,
    /// tooltips, menus, dialogs, navigation items. Lists of data (ListView, GridView) are skipped, and so
    /// is anything tagged "NoLoc".
    /// </summary>
    public static void Translate(object? node)
    {
        if (Current == "en" || node == null)
            return;
        Walk(node, 0);
    }

    private static string? Tr(object? value) => value is string s ? T(s) : null;

    private static void Walk(object? node, int depth)
    {
        if (node == null || depth > 64)
            return;
        if (node is FrameworkElement { Tag: "NoLoc" })
            return;
        if (node is UIElement element && ToolTipService.GetToolTip(element) is string tip)
            ToolTipService.SetToolTip(element, T(tip));

        switch (node)
        {
            case MenuFlyout flyout:
                foreach (MenuFlyoutItemBase item in flyout.Items)
                    Walk(item, depth + 1);
                return;
            case MenuBar bar:
                foreach (MenuBarItem top in bar.Items)
                {
                    top.Title = T(top.Title);
                    foreach (MenuFlyoutItemBase item in top.Items)
                        Walk(item, depth + 1);
                }
                return;
            case MenuFlyoutSubItem sub:
                sub.Text = T(sub.Text);
                foreach (MenuFlyoutItemBase item in sub.Items)
                    Walk(item, depth + 1);
                return;
            case MenuFlyoutItem menuItem: // also toggle and radio items
                menuItem.Text = T(menuItem.Text);
                return;
            case ContentDialog dialog:
                if (Tr(dialog.Title) is { } title)
                    dialog.Title = title;
                dialog.PrimaryButtonText = T(dialog.PrimaryButtonText);
                dialog.SecondaryButtonText = T(dialog.SecondaryButtonText);
                dialog.CloseButtonText = T(dialog.CloseButtonText);
                Walk(dialog.Content, depth + 1);
                return;
            case NavigationView nav:
                foreach (object item in nav.MenuItems)
                    if (item is NavigationViewItem nvi && Tr(nvi.Content) is { } content)
                        nvi.Content = content;
                return; // the page itself is translated when it is built
            case TextBlock text:
                if (text.Inlines.Count <= 1 && !string.IsNullOrEmpty(text.Text))
                    text.Text = T(text.Text);
                return;
            case TextBox box:
                if (Tr(box.Header) is { } boxHeader)
                    box.Header = boxHeader;
                box.PlaceholderText = T(box.PlaceholderText);
                return;
            case PasswordBox password:
                if (Tr(password.Header) is { } passwordHeader)
                    password.Header = passwordHeader;
                password.PlaceholderText = T(password.PlaceholderText);
                return;
            case NumberBox number:
                if (Tr(number.Header) is { } numberHeader)
                    number.Header = numberHeader;
                number.PlaceholderText = T(number.PlaceholderText);
                return;
            case ComboBox combo: // items are data (the code that fills them translates them)
                if (Tr(combo.Header) is { } comboHeader)
                    combo.Header = comboHeader;
                combo.PlaceholderText = T(combo.PlaceholderText);
                return;
            case RadioButtons radios:
                if (Tr(radios.Header) is { } radiosHeader)
                    radios.Header = radiosHeader;
                foreach (object item in radios.Items)
                    Walk(item, depth + 1);
                return;
            case ToggleSwitch toggle:
                if (Tr(toggle.Header) is { } toggleHeader)
                    toggle.Header = toggleHeader;
                if (Tr(toggle.OnContent) is { } on)
                    toggle.OnContent = on;
                if (Tr(toggle.OffContent) is { } off)
                    toggle.OffContent = off;
                return;
            case InfoBar info:
                info.Title = T(info.Title);
                info.Message = T(info.Message);
                Walk(info.ActionButton, depth + 1);
                return;
            case ItemsControl: // ListView, GridView, ...: data
                return;
            case ContentControl control: // Button, CheckBox, RadioButton, ScrollViewer, ...
                if (control.Content is string s)
                    control.Content = T(s);
                else
                    Walk(control.Content, depth + 1);
                if (control is Button { Flyout: MenuFlyout buttonFlyout })
                    Walk(buttonFlyout, depth + 1);
                return;
            case Panel panel:
                foreach (UIElement child in panel.Children)
                    Walk(child, depth + 1);
                return;
            case Border border:
                Walk(border.Child, depth + 1);
                return;
            case UserControl user:
                Walk(user.Content, depth + 1);
                return;
            case ContentPresenter presenter:
                if (presenter.Content is string ps)
                    presenter.Content = T(ps);
                else
                    Walk(presenter.Content, depth + 1);
                return;
            case Viewbox viewbox:
                Walk(viewbox.Child, depth + 1);
                return;
        }
    }
}
