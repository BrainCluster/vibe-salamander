# Vibe Salamander – Architecture

Vibe Salamander is a spiritual successor to Altap/Open Salamander: a fast, precise, keyboard-first
**dual-pane** file manager for Windows, rebuilt on C# / WinUI 3 with a Fluent design language in the
spirit of the [Files](https://github.com/files-community/files) app.

> Vibe Salamander is to Altap Salamander what Files is to Windows File Explorer.

## Principles

| Classic (keep)                                  | Modern (add)                                         |
|-------------------------------------------------|------------------------------------------------------|
| Dual-pane only, Salamander layout and hotkeys   | Fluent/Mica look, light/dark, rounded, accent colors  |
| Precision: exact sizes, dates, attributes        | Density scaling: *Compact* (Salamander) ↔ *Comfortable* |
| Speed: instant listings, no UI stalls            | Async everything, virtualized lists, per-monitor DPI |
| Keyboard first, mouse optional                   | Flexible layout (toggle bars), customizable keys      |
| Plugin architecture (archives, FS, viewers)      | Managed addon model (.NET assemblies, isolated load)  |

Open Salamander (`C:\dev\source\repos\OpenSalamander`) is the behavioral reference: when in doubt about
how a command should behave, check its implementation there (`src/fileswn*.cpp`, `src/menu4.cpp`, …).

## Solution layout

```
VibeSalamander.sln
├─ src/
│  ├─ VibeSalamander.Core                  UI-free engine (net10.0-windows)
│  │   ├─ FileSystem/   providers (local disk, This PC), entries, paths
│  │   ├─ Panels/       sorting, navigation history
│  │   ├─ Commands/     command ids + default Salamander key map
│  │   ├─ Formatting/   sizes, dates, attributes (Salamander formats)
│  │   ├─ Interop/      Win32/Shell P/Invoke (copy/move/delete, properties)
│  │   └─ Settings/     JSON settings in %AppData%\VibeSalamander
│  ├─ VibeSalamander.Plugins.Abstractions  plugin contracts (no UI deps)
│  ├─ VibeSalamander.Plugins.Archive       built-in archive plugin (pack / unpack)
│  └─ VibeSalamander.App                   WinUI 3 desktop app (unpackaged, self-contained WinAppSDK)
│      ├─ ViewModels/   PaneViewModel, FileItemViewModel, MainViewModel
│      ├─ Views/        FilePane (one panel), dialogs
│      └─ Services/     IconCache (shell icons → WinUI bitmaps)
└─ tests/
   └─ VibeSalamander.Core.Tests            xUnit tests for the engine
```

The **Core** has no WinUI dependency so it is testable and reusable (a CLI, a future
Avalonia/other front-end, or plugin hosts can use it).

## Main window layout (mirrors Salamander)

```
┌ Title bar (Mica, custom) ──────────────────────────────────────────────┐
│ Menu: Left  Files  Edit  Commands  Plugins  Options  Right  Help       │
│ Top toolbar (compact Fluent icon buttons)                              │
├──────────────── Left panel ───────────┬──────────── Right panel ───────┤
│ Drive bar                             │ Drive bar                      │
│ Address bar  ⌂ This PC › C: › Users › │ Address bar                    │
│ Header line     Name Ext Size Date …  │ Header line                    │
│ File list (virtualized)               │ File list                      │
│ Information line (focused item)       │ Information line               │
├───────────────────────────────────────┴────────────────────────────────┤
│ Transfers (shown while it has items; resizable, collapsible)           │
│  Copy 3 items  C:\a → D:\b   ███████░░░ 70 %   12 MB/s  0:14 left  ‖ ✕  │
├────────────────────────────────────────────────────────────────────────┤
│ Command line                                                           │
│ F1 Help  F2 Rename  F3 View  F4 Edit  F5 Copy  F6 Move  F7 MakeDir …    │
│ (holding Ctrl / Alt / Shift relabels F1–F12 with those shortcuts)      │
└────────────────────────────────────────────────────────────────────────┘
```

Every bar can be toggled (Options → Show), as in Salamander.

## Key design decisions

* **Caret vs. marks.** Salamander separates the *focused item* (caret) from *selected/marked* items
  (Insert/Space/Num+). The ListView's single selection is used only as the caret; marks are a
  property of `FileItemViewModel` rendered with the Salamander "selected" color.
* **Fast listing.** `FileSystemEnumerable<T>` (NtQueryDirectoryFile under the hood) with a transform
  that builds `FileEntry` structs directly; no `FileInfo` allocations. Listing runs off the UI thread
  and the result replaces the collection in one assignment (no per-item change notifications).
* **Providers.** Every panel path belongs to an `IFileSystemProvider`: `LocalFileSystemProvider` for
  disk paths and `ThisPcProvider` for the drive list (`..` in a drive root goes there, as in the
  Open Salamander "This PC" plugin). Archives, FTP, etc. become additional providers via addons.
* **Commands.** All actions are `CommandId`s dispatched by `MainViewModel`. Menus, toolbar,
  F-key bar and keyboard shortcuts all map to the same ids, so key customization and addon commands
  plug into one place.
* **File operations.** `FileOperationEngine` (CopyFileEx, plan-then-execute, pause/cancel) runs every
  copy/move/permanent delete as an item of `TransfersViewModel`, a background queue (3 in parallel)
  shown in the transfer panel; nothing is modal. Engine questions (overwrite, errors) are marshalled
  to the UI thread and go through one dialog gate in `Dialogs`, so prompts from several transfers and
  the user's own dialogs never collide. Recycle Bin deletes still use the shell (`SHFileOperation`).
* **Icons.** Folder/file glyphs render immediately; real shell icons load asynchronously per
  extension (per file for `.exe/.lnk/.ico`) and are cached.
* **Density.** One `RowHeight` setting (Compact 20 px ≈ Salamander, Comfortable 28 px ≈ Files) drives
  list, header and bar sizes.

## Plugins

`VibeSalamander.Plugins.Abstractions` holds the contracts. A plugin is a .NET assembly with a public
class implementing `IVibePlugin`; in `Initialize(IPluginHost)` it registers what it adds:

| Salamander plugin type | Vibe contract           | Status / examples                                   |
|------------------------|--------------------------|-----------------------------------------------------|
| Archiver               | `IArchiver`              | **Archives** (built-in): pack ZIP/TAR/TAR.GZ, unpack ZIP/7z/RAR/TAR/gz/bz2/xz/lz |
| Menu extension         | `ICommandContributor`    | Plugins menu commands (checksum, renamer, … later)  |
| Picture window         | `IPluginUi.OpenCanvasWindow` | **Disk Map** (built-in): the plugin draws pixels and labels, the host shows them with views, legend, tips and outlines |
| File system            | `IPluginFileSystem`      | **FTP / SFTP** (built-in): servers in the panels (`RemoteFileSystemProvider`) |
| Viewer                 | (later)                  | picture viewer                                      |

* **Loading** (`Services/PluginManager`): built-in plugins are referenced by the app; others are
  found in `<app>\Plugins\<name>\` and `%AppData%\VibeSalamander\Plugins\<name>\` (`<name>.dll`, or
  every assembly in the folder), each in its own `AssemblyLoadContext` that shares Core and the
  contracts with the app. Disabled plugins (Configuration → Plugins) are listed but not initialized.
* **Pack / unpack** run in the transfer panel like copies: `TransfersViewModel.EnqueueJob` gives the
  plugin an `IArchiveJob` (progress, pause, cancel, conflict/error/password questions marshalled to
  the UI). `ProgressTracker` turns byte counts into speed and remaining time.
* **Archive plugin** (`VibeSalamander.Plugins.Archive`): packs with .NET (`ZipArchive`, `TarWriter`
  + `GZipStream`) into a temporary file that replaces the target when complete; unpacks ZIP/7z/RAR
  with SharpCompress 0.49.1 (MIT; solid archives in one pass), TAR variants with .NET's `TarReader`
  (pax/GNU/ustar) over gzip / bzip2 / xz / lzip streams. Entry names that would leave the target
  directory are skipped; a wrong password is asked again.
* **Archives as directories** (`Services/ArchiveFileSystemProvider`, registered before the disk
  provider): a path like `C:\Downloads\photos.zip\2024` is the `2024` directory inside photos.zip. The
  archiver's `ListAsync` gives the items once per archive version (cached by size and time); the
  provider builds the directory tree. The panel is read-only there: F5 unpacks the selected items
  (`UnpackRequest.Items` + `BasePath`), Enter/F3 unpacks one file to `%TEMP%\VibeSalamander\FromArchives`
  and opens it.

## Languages

`Services/Loc` holds the user interface language (Configuration → General → Language; "" = the
Windows language). Translations are `src/VibeSalamander.App/Strings/<code>.json` files mapping the
English text to the translation (`{ "Copy": "Kopiraj" }`); a missing entry stays English.

* Code uses `Loc.T("text")` / `Loc.F("format {0}", value)` where it builds texts (the core uses
  `Core.Localization.L`, wired to `Loc` at startup).
* XAML, dialogs and menus are translated by walking their elements (`Loc.Translate`): text blocks,
  buttons, headers, placeholders, tooltips, menu items, navigation items. Lists of data (ListView)
  and elements tagged `NoLoc` (names and paths) are skipped.
* Entries with placeholders also translate texts made from them ("Copy {0} to:" matches
  "Copy "a.txt" to:"), when their fixed text is distinctive (6+ letters).
* Adding a language: copy a `Strings\*.json`, translate the values, add the code to `Loc.Languages`.

