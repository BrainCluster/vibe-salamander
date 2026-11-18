// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Core.Viewing;
using VibeSalamander.Services;

namespace VibeSalamander.Views;

// Dialogs: Convert (Ctrl+K), Salamander's line endings and encodings of text files.
public sealed partial class Dialogs
{
    /// <summary>What to convert the files to; null: cancelled.</summary>
    public async Task<ConversionRequest?> ConvertAsync(string what)
    {
        IReadOnlyList<TextEncoding> encodings = TextEncodings.Choices();
        var endings = new ComboBox { Header = "Line endings", HorizontalAlignment = HorizontalAlignment.Stretch };
        endings.Items.Add(Loc.T("Keep as they are"));
        endings.Items.Add(Loc.T("Windows (CR LF)"));
        endings.Items.Add(Loc.T("Unix, macOS (LF)"));
        endings.Items.Add(Loc.T("Classic Mac (CR)"));
        endings.SelectedIndex = 1;

        var encoding = new ComboBox { Header = "Encoding", HorizontalAlignment = HorizontalAlignment.Stretch };
        encoding.Items.Add(Loc.T("Keep as it is"));
        foreach (TextEncoding e in encodings)
            encoding.Items.Add(e.Name);
        encoding.SelectedIndex = 0;

        var readAs = new ComboBox { Header = "The files are in", HorizontalAlignment = HorizontalAlignment.Stretch };
        readAs.Items.Add(Loc.T("Recognize from each file"));
        foreach (TextEncoding e in encodings)
            readAs.Items.Add(e.Name);
        readAs.SelectedIndex = 0;

        var body = new StackPanel { Spacing = 10, MinWidth = 380 };
        body.Children.Add(Text(what, secondary: true));
        body.Children.Add(endings);
        body.Children.Add(encoding);
        body.Children.Add(readAs);
        body.Children.Add(Text("Files that aren't text, or that would lose characters in the new encoding, are left as they are.", secondary: true));

        ContentDialog dialog = NewDialog("Convert", body);
        dialog.PrimaryButtonText = "Convert";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        // Enter converts (the drop-downs open with Alt+Down or F4, or a click), as in the other dialogs
        bool enter = false;
        foreach (ComboBox box in new[] { endings, encoding, readAs })
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter && !((ComboBox)s).IsDropDownOpen)
                {
                    e.Handled = true;
                    enter = true;
                    dialog.Hide();
                }
            };
        if (await ShowAsync(dialog) != ContentDialogResult.Primary && !enter)
            return null;
        LineEnding? ending = endings.SelectedIndex switch
        {
            1 => LineEnding.CrLf,
            2 => LineEnding.Lf,
            3 => LineEnding.Cr,
            _ => null,
        };
        TextEncoding? Pick(ComboBox box) => box.SelectedIndex > 0 ? encodings[box.SelectedIndex - 1] : null;
        return new ConversionRequest(ending, Pick(encoding), Pick(readAs));
    }
}
