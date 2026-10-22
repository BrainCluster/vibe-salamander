// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Plugins.DiskMap;

/// <summary>What a file is (its color in the Disk Map and Blocks views).</summary>
internal enum FileKind : byte
{
    Other,
    Images,
    Video,
    Audio,
    Documents,
    Archives,
    Programs,
    Code,
    Data,
}

internal static class FileKinds
{
    public static readonly FileKind[] All =
    {
        FileKind.Images, FileKind.Video, FileKind.Audio, FileKind.Documents, FileKind.Archives,
        FileKind.Programs, FileKind.Code, FileKind.Data, FileKind.Other,
    };

    private static readonly Dictionary<string, FileKind> ByExtension = Build();

    private static Dictionary<string, FileKind> Build()
    {
        var map = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        void Add(FileKind kind, string extensions)
        {
            foreach (string extension in extensions.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[extension] = kind;
        }
        Add(FileKind.Images, "jpg jpeg jfif png gif bmp webp heic heif tif tiff ico svg psd raw cr2 cr3 nef arw dng orf rw2 avif jxl xcf ai eps");
        Add(FileKind.Video, "mp4 m4v mkv avi mov wmv webm flv mpg mpeg ts mts m2ts vob 3gp ogv");
        Add(FileKind.Audio, "mp3 flac wav aac ogg oga opus m4a wma aiff aif mid midi ape alac");
        Add(FileKind.Documents, "pdf doc docx docm xls xlsx xlsm ppt pptx odt ods odp rtf txt md csv epub mobi djvu one pub vsd vsdx tex log");
        Add(FileKind.Archives, "zip 7z rar tar gz tgz bz2 xz lz lzma zst cab iso img vhd vhdx vmdk qcow2 dmg wim esd jar apk nupkg");
        Add(FileKind.Programs, "exe dll sys msi msix msixbundle appx appxbundle ocx drv cpl scr com efi mui so dylib winmd");
        Add(FileKind.Code, "cs cpp cc cxx h hpp c js mjs ts tsx jsx py java kt go rs rb php swift json xml html htm css scss xaml ps1 psm1 bat cmd sh yml yaml toml ini sln csproj vcxproj props targets sql lua pdb obj lib pch ipch");
        Add(FileKind.Data, "db sqlite sqlite3 mdb accdb dat bin pak cache tmp etl evtx pf blf regtrans-ms vdi bak dmp mdmp hiberfil edb jrs chk idx pack sav");
        return map;
    }

    public static FileKind Of(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
            return FileKind.Other;
        if (name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) && name.Contains("file", StringComparison.OrdinalIgnoreCase))
            return FileKind.Data; // pagefile.sys, hiberfil.sys, swapfile.sys: the memory's room on the disk
        return ByExtension.TryGetValue(name[(dot + 1)..], out FileKind kind) ? kind : FileKind.Other;
    }

    /// <summary>The English name (translated by the plugin).</summary>
    public static string Name(FileKind kind) => kind switch
    {
        FileKind.Images => "Pictures",
        FileKind.Video => "Videos",
        FileKind.Audio => "Music",
        FileKind.Documents => "Documents",
        FileKind.Archives => "Archives and disk images",
        FileKind.Programs => "Programs",
        FileKind.Code => "Code",
        FileKind.Data => "Data and system",
        _ => "Other",
    };

    /// <summary>The kind's color (0xRRGGBB): a calm, modern palette that reads in light and dark themes.</summary>
    public static uint Color(FileKind kind) => kind switch
    {
        FileKind.Images => 0xF2A33A,
        FileKind.Video => 0xE5534B,
        FileKind.Audio => 0xD95FA6,
        FileKind.Documents => 0x4A8FE7,
        FileKind.Archives => 0x8E6BE8,
        FileKind.Programs => 0x2FB383,
        FileKind.Code => 0x22B5C9,
        FileKind.Data => 0x93B83C,
        _ => 0x8D99AB,
    };
}
