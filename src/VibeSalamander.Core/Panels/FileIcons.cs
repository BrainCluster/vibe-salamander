// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Core.Panels;

/// <summary>What kind of file an item is, for the icon packs that draw their own icons (Configuration → Appearance).</summary>
public enum FileKind
{
    Other,
    Folder,
    Picture,
    Video,
    Audio,
    Archive,
    DiskImage,
    Code,
    Document,
    Spreadsheet,
    Presentation,
    Pdf,
    Text,
    Executable,
    Shortcut,
    Font,
}

/// <summary>The kinds of files by extension, and their icons (Segoe Fluent Icons) and colors.</summary>
public static class FileIcons
{
    private static readonly Dictionary<string, FileKind> Kinds = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var kinds = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string extensions)
        {
            foreach (string e in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                kinds[e] = kind;
        }
        Add(FileKind.Picture, "jpg jpeg jfif png gif bmp webp tif tiff heic heif avif ico jxl svg raw dng cr2 cr3 nef arw orf rw2 raf psd");
        Add(FileKind.Video, "mp4 mkv avi mov wmv webm m4v mpg mpeg 3gp mts m2ts flv");
        Add(FileKind.Audio, "mp3 wav flac aac m4a ogg opus wma aiff aif mid midi");
        Add(FileKind.Archive, "zip 7z rar tar gz tgz bz2 tbz2 xz txz lz lzma zst cab arj");
        Add(FileKind.DiskImage, "iso img vhd vhdx");
        Add(FileKind.Code, "cs js mjs ts tsx jsx py java c cpp cc h hpp go rs rb php swift kt kts sh bash ps1 psm1 bat cmd json xml yaml yml toml html htm css scss less sql lua dart vb fs xaml csproj vbproj fsproj sln props targets ipynb");
        Add(FileKind.Document, "doc docx odt rtf md markdown pages epub");
        Add(FileKind.Spreadsheet, "xls xlsx xlsm ods csv tsv numbers");
        Add(FileKind.Presentation, "ppt pptx odp key");
        Add(FileKind.Pdf, "pdf");
        Add(FileKind.Text, "txt log ini cfg conf nfo diz");
        Add(FileKind.Executable, "exe msi com scr appx msix msixbundle");
        Add(FileKind.Shortcut, "lnk url");
        Add(FileKind.Font, "ttf otf ttc woff woff2 fon");
        return kinds;
    }

    public static FileKind KindOf(FileEntry entry) =>
        entry.IsDirectory ? FileKind.Folder : KindOf(entry.Extension);

    /// <summary>By extension ("jpg", with or without the dot).</summary>
    public static FileKind KindOf(string extension) =>
        Kinds.TryGetValue(extension.TrimStart('.'), out FileKind kind) ? kind : FileKind.Other;

    public static string Glyph(FileKind kind) => kind switch
    {
        FileKind.Folder => "",
        FileKind.Picture => "",
        FileKind.Video => "",
        FileKind.Audio => "",
        FileKind.Archive => "",
        FileKind.DiskImage => "",
        FileKind.Code => "",
        FileKind.Document => "",
        FileKind.Spreadsheet => "",
        FileKind.Presentation => "",
        FileKind.Pdf => "",
        FileKind.Text => "",
        FileKind.Executable => "",
        FileKind.Shortcut => "",
        FileKind.Font => "",
        _ => "",
    };

    /// <summary>The color of the kind in the colored pack (0xRRGGBB), or null: the panel's secondary text color.</summary>
    public static uint? Color(FileKind kind) => kind switch
    {
        FileKind.Picture => 0x2EA36B,
        FileKind.Video => 0xE0662B,
        FileKind.Audio => 0xA866D6,
        FileKind.Archive => 0xC9962A,
        FileKind.DiskImage => 0x7A8A99,
        FileKind.Code => 0x3B8EEA,
        FileKind.Document => 0x2B6FD6,
        FileKind.Spreadsheet => 0x1E9E55,
        FileKind.Presentation => 0xD9573B,
        FileKind.Pdf => 0xE03B3B,
        FileKind.Executable => 0x5B7CE0,
        FileKind.Shortcut => 0x29A3A3,
        FileKind.Font => 0xB0708F,
        _ => null,
    };
}
