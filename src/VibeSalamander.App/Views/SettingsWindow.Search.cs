// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using VibeSalamander.Services;

namespace VibeSalamander.Views;

/// <summary>
/// Search in the Configuration window (Ctrl+F): every page is searched in the user's language (labels,
/// descriptions, group titles and the choices of drop-downs). The matching options are shown together,
/// grouped by page and group, and work right there; pages that match only in their lists (Keyboard,
/// User Menu, ...) are listed as links.
/// </summary>
public sealed partial class SettingsWindow
{
    private AutoSuggestBox? _search;
    private DispatcherQueueTimer? _searchTimer;
    private bool _searching;

    private void SetUpSearch()
    {
        _search = new AutoSuggestBox
        {
            PlaceholderText = Loc.T("Search settings"),
            QueryIcon = new SymbolIcon(Symbol.Find),
            Tag = "NoLoc",
        };
        _search.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
                return;
            _searchTimer ??= CreateSearchTimer();
            _searchTimer.Stop();
            _searchTimer.Start();
        };
        _search.QuerySubmitted += (_, _) => RunSearch();
        _search.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _search.Text.Length > 0)
            {
                e.Handled = true;
                ClearSearch();
                Render(_currentPage);
            }
        };
        Nav.AutoSuggestBox = _search;

        // Ctrl+F (and Ctrl+E, like Windows Settings) go to the search box
        foreach (Windows.System.VirtualKey key in new[] { Windows.System.VirtualKey.F, Windows.System.VirtualKey.E })
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = Windows.System.VirtualKeyModifiers.Control };
            accelerator.Invoked += (_, args) =>
            {
                args.Handled = true;
                _search.Focus(FocusState.Keyboard);
            };
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
        // clicking the page that is already selected leaves the search too
        Nav.ItemInvoked += (_, args) =>
        {
            if (_searching && args.InvokedItemContainer?.Tag is string tag)
            {
                ClearSearch();
                Render(tag);
            }
        };
    }

    private DispatcherQueueTimer CreateSearchTimer()
    {
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => RunSearch();
        return timer;
    }

    private void ClearSearch()
    {
        _searchTimer?.Stop();
        _searching = false;
        if (_search != null && _search.Text.Length > 0)
            _search.Text = string.Empty;
    }

    private void RunSearch()
    {
        string query = _search?.Text.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            if (_searching)
            {
                _searching = false;
                Render(_currentPage);
            }
            return;
        }
        _searching = true;
        PageHost.Content = BuildSearchResults(query);
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>The matching options of all pages, under the page's and the group's names.</summary>
    private UIElement BuildSearchResults(string query)
    {
        string[] words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Matches(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            string lower = text.ToLowerInvariant();
            return words.All(w => lower.Contains(w, StringComparison.Ordinal));
        }

        var results = Page(Loc.T("Search results"));
        Style? cardStyle = ThemedStyle("SettingsCardStyle");
        object? sectionStyle = Application.Current.Resources.TryGetValue("BodyStrongTextBlockStyle", out object? strong) ? strong : null;
        int found = 0;
        _loading = true;
        try
        {
            foreach (NavigationViewItem item in Nav.MenuItems.OfType<NavigationViewItem>())
            {
                if (item.Tag is not string tag)
                    continue;
                string pageName = item.Content as string ?? tag;
                UIElement built = BuildPage(tag);
                Loc.Translate(built);

                var matches = new List<(string? Section, UIElement Element)>();
                bool elsewhere = false; // a match in a list or table: the page is linked
                StackPanel? page = built as StackPanel;
                if (page != null)
                {
                    string? section = null;
                    bool sectionMatches = false;
                    foreach (UIElement child in page.Children.Skip(1).ToList())
                    {
                        if (child is TextBlock heading && ReferenceEquals(heading.Style, sectionStyle))
                        {
                            section = heading.Text;
                            sectionMatches = Matches(section);
                            continue;
                        }
                        if (child is Border card && ReferenceEquals(card.Style, cardStyle))
                        {
                            if (sectionMatches || Matches(string.Join(' ', Texts(card))))
                                matches.Add((section, card));
                        }
                        else if (child is not InfoBar && Matches(string.Join(' ', Texts(child))))
                        {
                            elsewhere = true;
                        }
                    }
                }
                else
                {
                    elsewhere = Matches(string.Join(' ', Texts(built)));
                }
                if (matches.Count == 0 && !elsewhere && !Matches(pageName))
                    continue;

                found += Math.Max(1, matches.Count);
                results.Children.Add(PageLink(pageName, tag, matches.Count == 0));
                string? lastSection = null;
                foreach (var (section, element) in matches)
                {
                    if (section != null && section != lastSection)
                        results.Children.Add(new TextBlock { Text = section, Opacity = 0.7, FontSize = 12, Margin = new Thickness(2, 6, 0, 0), Tag = "NoLoc" });
                    lastSection = section;
                    // the option itself moves here: it changes the setting like on its page
                    page!.Children.Remove(element);
                    results.Children.Add(element);
                }
            }
        }
        finally
        {
            _loading = false;
        }
        if (found == 0)
            results.Children.Add(new TextBlock { Text = Loc.F("No settings match \"{0}\".", query), Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        return results;
    }

    /// <summary>"Panels ›": goes to the page (the search ends).</summary>
    private HyperlinkButton PageLink(string pageName, string tag, bool onlyHere)
    {
        var text = new TextBlock
        {
            Text = onlyHere ? pageName + "  ›  " + Loc.T("matches on this page") : pageName + "  ›",
            Style = Application.Current.Resources["BodyStrongTextBlockStyle"] as Style,
        };
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(2, 4, 2, 4), Margin = new Thickness(0, 14, 0, 0), Tag = "NoLoc" };
        link.Click += (_, _) =>
        {
            ClearSearch();
            ShowPage(tag);
            Render(tag); // also when it is the page already selected
        };
        return link;
    }

    /// <summary>The texts in an element: labels, descriptions, headers, check boxes and the choices of drop-downs.</summary>
    private static IEnumerable<string> Texts(object? element, int depth = 0)
    {
        if (element == null || depth > 12)
            yield break;
        switch (element)
        {
            case string text:
                yield return text;
                yield break;
            case TextBlock block:
                yield return block.Text;
                yield break;
            case TextBox box:
                if (box.Header is string boxHeader)
                    yield return boxHeader;
                yield break;
            case ComboBox combo:
                if (combo.Header is string comboHeader)
                    yield return comboHeader;
                foreach (object choice in combo.Items)
                    foreach (string t in Texts(choice is ComboBoxItem comboItem ? comboItem.Content : choice, depth + 1))
                        yield return t;
                yield break;
            case ItemsControl list:
                foreach (object entry in list.Items.Take(500))
                    foreach (string t in Texts(entry is ContentControl c ? c.Content : entry, depth + 1))
                        yield return t;
                yield break;
            case ContentControl content:
                foreach (string t in Texts(content.Content, depth + 1))
                    yield return t;
                yield break;
            case Border border:
                foreach (string t in Texts(border.Child, depth + 1))
                    yield return t;
                yield break;
            case Panel panel:
                foreach (UIElement child in panel.Children)
                    foreach (string t in Texts(child, depth + 1))
                        yield return t;
                yield break;
        }
    }
}
