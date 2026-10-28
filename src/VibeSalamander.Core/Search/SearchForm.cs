// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Localization;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.Core.Search;

/// <summary>The Find window's fields (as typed, <see cref="SavedSearch"/>) turned into search criteria.</summary>
public static class SearchForm
{
    /// <summary>The attributes the window offers, with the letters used in <see cref="SavedSearch.Attributes"/>.</summary>
    public static IReadOnlyList<(char Letter, FileAttributes Attribute, string Name)> Attributes { get; } = new[]
    {
        ('R', FileAttributes.ReadOnly, "Read-only"),
        ('H', FileAttributes.Hidden, "Hidden"),
        ('S', FileAttributes.System, "System"),
        ('A', FileAttributes.Archive, "Archive"),
        ('C', FileAttributes.Compressed, "Compressed"),
        ('E', FileAttributes.Encrypted, "Encrypted"),
    };

    /// <summary>Folders often worth skipping (offered when the field is empty).</summary>
    public const string SuggestedSkipFolders = "node_modules;.git;.svn;bin;obj;$RECYCLE.BIN;System Volume Information";

    /// <summary>"R+H-": the required (+) and excluded (−) attributes.</summary>
    public static (FileAttributes Required, FileAttributes Excluded) ParseAttributes(string text)
    {
        FileAttributes required = 0, excluded = 0;
        for (int i = 0; i + 1 < text.Length; i += 2)
        {
            var match = Attributes.FirstOrDefault(a => a.Letter == char.ToUpperInvariant(text[i]));
            if (match.Name == null)
                continue;
            if (text[i + 1] == '+')
                required |= match.Attribute;
            else if (text[i + 1] == '-')
                excluded |= match.Attribute;
        }
        return (required, excluded);
    }

    public static string FormatAttributes(FileAttributes required, FileAttributes excluded) =>
        string.Concat(Attributes.Select(a => (required & a.Attribute) != 0 ? a.Letter + "+" : (excluded & a.Attribute) != 0 ? a.Letter + "-" : string.Empty));

    /// <summary>The folders of "Look in": separated by ';' (a folder name with ';' in it is rare).</summary>
    public static IReadOnlyList<string> Folders(string lookIn) =>
        lookIn.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .Select(f => f.Trim('"'))
              .Where(f => f.Length > 0)
              .ToList();

    /// <summary>The dates of a "Modified" choice, from <paramref name="now"/>; nulls for "Any".</summary>
    public static (DateTime? From, DateTime? To) Dates(SavedSearch form, DateTime now) => form.Modified switch
    {
        "Today" => (now.Date, null),
        "Week" => (now.Date.AddDays(-7), null),
        "Month" => (now.Date.AddDays(-30), null),
        "Year" => (now.Date.AddDays(-365), null),
        "Between" => (form.From?.Date, form.To?.Date.AddDays(1).AddTicks(-1)),
        _ => (null, null),
    };

    /// <summary>The criteria, or why they can't be made (a size that can't be read, an empty folder list...).</summary>
    public static SearchCriteria? Criteria(SavedSearch form, DateTime now, int parallelism, out string? problem)
    {
        problem = null;
        long? min = null, max = null;
        if (form.MinSize.Trim().Length > 0 && (min = SpeedLimits.ParseBytes(form.MinSize, 1)) == null)
            problem = L.F("\"{0}\" isn't a size (like 10 KB or 2.5 GB).", form.MinSize);
        if (form.MaxSize.Trim().Length > 0 && (max = SpeedLimits.ParseBytes(form.MaxSize, 1)) == null)
            problem = L.F("\"{0}\" isn't a size (like 10 KB or 2.5 GB).", form.MaxSize);
        if (problem != null)
            return null;
        var (from, to) = Dates(form, now);
        var (required, excluded) = ParseAttributes(form.Attributes);
        var criteria = new SearchCriteria
        {
            Directories = Folders(form.LookIn),
            Mask = new WildcardMask(string.IsNullOrWhiteSpace(form.Named) ? "*.*" : form.Named),
            MaxDepth = !form.Subdirectories ? 0 : form.Depth is int depth and >= 0 ? depth : -1,
            IncludeDirectories = form.Directories,
            IncludeHidden = form.Hidden,
            SkipFolders = string.IsNullOrWhiteSpace(form.SkipFolders) ? null : new WildcardMask(form.SkipFolders),
            Texts = form.Texts.Where(t => t.Text.Length > 0)
                .Select(t => new TextCondition(t.Text, Enum.TryParse(t.Mode, out TextMode mode) ? mode : TextMode.Text, t.Negate))
                .ToList(),
            AllTexts = form.AllTexts,
            MatchCase = form.MatchCase,
            WholeWord = form.WholeWord,
            MinSize = min,
            MaxSize = max,
            ModifiedFrom = from,
            ModifiedTo = to,
            RequiredAttributes = required,
            ExcludedAttributes = excluded,
            InsideArchives = form.InsideArchives,
            Parallelism = parallelism,
        };
        problem = criteria.Problem();
        if (problem == null)
            foreach (string folder in criteria.Directories)
                if (!Directory.Exists(folder))
                    problem = L.F("Directory not found: {0}", folder);
        return problem == null ? criteria : null;
    }

    /// <summary>A copy (saved searches are copies of the form, not the form itself).</summary>
    public static SavedSearch Copy(SavedSearch s, string? name = null) => new()
    {
        Name = name ?? s.Name,
        Named = s.Named,
        LookIn = s.LookIn,
        Texts = s.Texts.Select(t => new SavedText { Text = t.Text, Mode = t.Mode, Negate = t.Negate }).ToList(),
        AllTexts = s.AllTexts,
        MatchCase = s.MatchCase,
        WholeWord = s.WholeWord,
        Subdirectories = s.Subdirectories,
        Directories = s.Directories,
        Hidden = s.Hidden,
        InsideArchives = s.InsideArchives,
        MinSize = s.MinSize,
        MaxSize = s.MaxSize,
        Modified = s.Modified,
        From = s.From,
        To = s.To,
        Attributes = s.Attributes,
        Depth = s.Depth,
        SkipFolders = s.SkipFolders,
    };
}
