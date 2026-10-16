// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Plugins;
using VibeSalamander.Views;
using Windows.ApplicationModel.DataTransfer;

namespace VibeSalamander.Services;

/// <summary>
/// The user interface the app lends to plugins (IPluginUi): their windows, dialogs, the clipboard and the
/// translations. Plugins may call it from any thread; everything runs on the UI thread here.
/// </summary>
public sealed class PluginUi : IPluginUi
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<ElementTheme> _theme;
    private readonly Dialogs _mainDialogs;
    private readonly List<PluginListWindow> _windows = new();

    public PluginUi(DispatcherQueue dispatcher, Func<ElementTheme> theme, Dialogs mainDialogs)
    {
        _dispatcher = dispatcher;
        _theme = theme;
        _mainDialogs = mainDialogs;
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        if (_dispatcher.HasThreadAccess)
            return work();
        var done = new TaskCompletionSource<T>();
        if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    done.TrySetResult(await work());
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }))
            done.TrySetCanceled();
        return done.Task;
    }

    private Dialogs DialogsFor(IPluginListWindow? owner) =>
        owner is PluginListWindow { IsClosed: false } window ? window.Dialogs : _mainDialogs;

    public IPluginListWindow OpenListWindow(string title, IReadOnlyList<PluginColumn> columns)
    {
        PluginListWindow Create()
        {
            var window = new PluginListWindow(title, columns, _theme());
            _windows.Add(window);
            window.Closed += (_, _) => _windows.Remove(window);
            window.Activate();
            return window;
        }
        if (_dispatcher.HasThreadAccess)
            return Create();
        return OnUiAsync(() => Task.FromResult(Create())).GetAwaiter().GetResult();
    }

    /// <summary>Closes every plugin window (the app is closing).</summary>
    public void CloseAll()
    {
        foreach (PluginListWindow window in _windows.ToList())
            window.Close();
    }

    private static TextBlock Text(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Tag = "NoLoc", // plugins translate their own texts (T)
        MaxWidth = 520,
    };

    public Task<bool> ConfirmAsync(string title, string message, IPluginListWindow? owner = null) => OnUiAsync(async () =>
    {
        Dialogs dialogs = DialogsFor(owner);
        ContentDialog dialog = dialogs.Create(title, Text(message));
        dialog.PrimaryButtonText = Loc.T("Yes");
        dialog.CloseButtonText = Loc.T("No");
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await dialogs.ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    });

    public Task ShowMessageAsync(string title, string message, IPluginListWindow? owner = null) => OnUiAsync(async () =>
    {
        Dialogs dialogs = DialogsFor(owner);
        ContentDialog dialog = dialogs.Create(title, Text(message));
        dialog.CloseButtonText = "OK";
        dialog.DefaultButton = ContentDialogButton.Close;
        await dialogs.ShowDialogAsync(dialog);
        return true;
    });

    public Task<bool> EditOptionsAsync(string title, string? description, IReadOnlyList<PluginOption> options, IPluginListWindow? owner = null) =>
        OnUiAsync(async () =>
        {
            var body = new StackPanel { Spacing = 2, MinWidth = 320 };
            if (!string.IsNullOrEmpty(description))
                body.Children.Add(Text(description));
            var boxes = options.Select(o => new CheckBox { Content = o.Label, IsChecked = o.Value, Tag = "NoLoc" }).ToList();
            foreach (CheckBox box in boxes)
                body.Children.Add(box);
            Dialogs dialogs = DialogsFor(owner);
            ContentDialog dialog = dialogs.Create(title, body);
            dialog.PrimaryButtonText = "OK";
            dialog.CloseButtonText = "Cancel";
            dialog.DefaultButton = ContentDialogButton.Primary;
            if (await dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
                return false;
            for (int i = 0; i < options.Count; i++)
                options[i].Value = boxes[i].IsChecked == true;
            return true;
        });

    public Task<PluginSaveChoice?> AskSaveFileAsync(string title, string suggestedPath, IReadOnlyList<PluginFileType> types, int selectedType,
        IPluginListWindow? owner = null) => OnUiAsync<PluginSaveChoice?>(async () =>
    {
        if (types.Count == 0)
            return null;
        var format = new ComboBox { Header = Loc.T("Format"), MinWidth = 220, Tag = "NoLoc" };
        foreach (PluginFileType type in types)
            format.Items.Add($"{type.Name} (*{type.Extension})");
        format.SelectedIndex = Math.Clamp(selectedType, 0, types.Count - 1);
        var path = new TextBox { Header = Loc.T("File name"), Text = suggestedPath, MinWidth = 460, Tag = "NoLoc" };
        var browse = new Button { Content = Loc.T("Browse..."), VerticalAlignment = VerticalAlignment.Bottom };

        // another format: the file name follows with its extension
        int lastFormat = format.SelectedIndex;
        format.SelectionChanged += (_, _) =>
        {
            if (format.SelectedIndex < 0 || lastFormat < 0)
                return;
            string oldExt = types[lastFormat].Extension, newExt = types[format.SelectedIndex].Extension;
            if (path.Text.EndsWith(oldExt, StringComparison.OrdinalIgnoreCase))
                path.Text = path.Text[..^oldExt.Length] + newExt;
            lastFormat = format.SelectedIndex;
        };

        Dialogs dialogs = DialogsFor(owner);
        Microsoft.UI.WindowId windowId = owner is PluginListWindow { IsClosed: false } window ? window.AppWindow.Id : default;
        browse.Click += async (_, _) =>
        {
            try
            {
                if (windowId.Value == 0)
                    return;
                PluginFileType type = types[Math.Max(0, format.SelectedIndex)];
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(windowId)
                {
                    SuggestedFileName = Path.GetFileNameWithoutExtension(path.Text),
                };
                picker.FileTypeChoices.Add(type.Name, new List<string> { type.Extension });
                var picked = await picker.PickSaveFileAsync();
                if (!string.IsNullOrEmpty(picked?.Path))
                    path.Text = picked.Path;
            }
            catch (Exception)
            {
                // no picker: the name is typed
            }
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(path);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        if (windowId.Value == 0)
            browse.Visibility = Visibility.Collapsed;

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(format);
        body.Children.Add(row);
        ContentDialog dialog = dialogs.Create(title, body);
        dialog.PrimaryButtonText = Loc.T("Save");
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.Opened += (_, _) =>
        {
            path.Focus(FocusState.Programmatic);
            int start = path.Text.Length - Path.GetFileName(path.Text).Length; // the name, not the folder or extension
            path.Select(start, Path.GetFileNameWithoutExtension(path.Text).Length);
        };
        if (await dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(path.Text))
            return null;
        return new PluginSaveChoice(path.Text.Trim(), Math.Max(0, format.SelectedIndex));
    });

    public void CopyToClipboard(string text)
    {
        void Copy()
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        if (_dispatcher.HasThreadAccess)
            Copy();
        else
            _dispatcher.TryEnqueue(Copy);
    }

    public string T(string english) => Loc.T(english);
}
