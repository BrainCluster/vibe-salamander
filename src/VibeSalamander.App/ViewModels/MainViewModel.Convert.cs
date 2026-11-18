// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Diagnostics;
using VibeSalamander.Core.Viewing;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

// MainViewModel: Convert (Ctrl+K), line endings and encodings of the marked text files.
public sealed partial class MainViewModel
{
    private async Task ConvertFilesAsync(PaneViewModel pane)
    {
        if (!pane.IsLocalDisk)
        {
            _host.Notify(Loc.T("Convert works on files on a disk."));
            return;
        }
        List<string> files = pane.GetOperationItems().Where(i => !i.Entry.IsDirectory && !i.Entry.IsUpDir && !i.IsDrive)
            .Select(i => i.Entry.FullPath).ToList();
        if (files.Count == 0)
        {
            _host.Notify(Loc.T("Mark the text files to convert (folders aren't converted)."));
            return;
        }
        string what = files.Count == 1 ? Loc.F("Convert \"{0}\":", Path.GetFileName(files[0])) : Loc.F("Convert {0} files:", files.Count);
        ConversionRequest? request = await _host.Dialogs.ConvertAsync(what);
        if (request == null)
            return;

        var outcomes = new List<(string File, ConversionOutcome Outcome, string? Problem)>();
        using (AppActivity.Begin("convert"))
        {
            await Task.Run(() =>
            {
                foreach (string file in files)
                {
                    var (outcome, problem) = TextConversion.Convert(file, request);
                    outcomes.Add((file, outcome, problem));
                }
            });
        }
        await pane.RefreshAsync();

        int converted = outcomes.Count(o => o.Outcome == ConversionOutcome.Converted);
        int unchanged = outcomes.Count(o => o.Outcome == ConversionOutcome.Unchanged);
        var notes = new List<string> { Loc.F("Converted: {0}", converted) };
        if (unchanged > 0)
            notes.Add(Loc.F("already so: {0}", unchanged));
        int notText = outcomes.Count(o => o.Outcome == ConversionOutcome.NotText);
        if (notText > 0)
            notes.Add(Loc.F("not text, left alone: {0}", notText));
        var problems = outcomes.Where(o => o.Outcome is ConversionOutcome.Unencodable or ConversionOutcome.Failed).ToList();
        foreach (var p in problems)
        {
            string text = $"{Path.GetFileName(p.File)}: {(p.Outcome == ConversionOutcome.Unencodable ? Loc.T("left alone, ") : string.Empty)}{p.Problem}";
            notes.Add(text);
            ErrorLog.Error("Convert " + text);
        }
        _host.Notify(string.Join("; ", notes), isError: problems.Count > 0);
    }
}
