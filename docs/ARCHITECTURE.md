# Vibe Salamander – Architecture

Vibe Salamander is a spiritual successor to Altap/Open Salamander: a fast, precise, keyboard-first
**dual-pane** file manager for Windows, rebuilt on C# / WinUI 3 with a Fluent design language in the
spirit of the [Files](https://github.com/files-community/files) app.

> Vibe Salamander is to Altap Salamander what Files is to Windows File Explorer.

This document describes the code as of **0.8.8**. Read it before changing anything; [ROADMAP.md](ROADMAP.md)
says what to build next. When the code and this document disagree, the code is right: fix the document in
the same commit.

## Principles

| Classic (keep)                                  | Modern (add)                                         |
|-------------------------------------------------|------------------------------------------------------|
| Dual-pane only, Salamander layout and hotkeys   | Fluent/Mica look, light/dark, rounded, accent colors  |
| Precision: exact sizes, dates, attributes        | Density scaling: *Compact* (Salamander) ↔ *Comfortable* |
| Speed: instant listings, no UI stalls            | Async everything, virtualized lists, per-monitor DPI |
| Keyboard first, mouse optional                   | Flexible layout (toggle bars), customizable keys      |
| Plugin architecture (archives, FS, commands)     | Managed plugin model (.NET assemblies, isolated load) |

Three more rules came out of the work so far and hold everywhere:

* **Nothing is modal that takes time.** Copies, packs, uploads and deletes run in the transfer panel; tools
  (Find, Compare, Checksums, Disk Map) are windows of their own; errors are notifications that close themselves.
* **Decisions live in Core, the app applies them.** What a command does in a given situation is a pure function
  in a `*Rules` class (`CopyTargetRules`, `PanelCommandRules`, `BreadcrumbRules`, `MarqueeRules`,
  `ContextMenuPlan`, `ArchiveRules`, …) with unit tests; the view model or view only gathers the facts and
  carries out the answer.
* **Plugins never touch the UI framework.** They talk to the app through `IPluginHost` / `IPluginUi` only.

[Open Salamander's source](https://github.com/OpenSalamander/salamander) is the behavioral reference: when in
doubt about how a command should behave, check its implementation there (`src/fileswn*.cpp`,
`src/menu4.cpp`, …). BrainCluster keeps a checkout at `C:\dev\source\repos\OpenSalamander`.

## Solution layout

```
VibeSalamander.sln                         (.NET 10, net10.0-windows10.0.22621.0, min Windows 10 2004)
├─ src/
│  ├─ VibeSalamander.Core                  UI-free engine (≈11,600 lines)
│  │   ├─ FileSystem/   IFileSystemProvider, FileEntry, local disk, This PC, found list, masks, paths
│  │   ├─ Panels/       sorting, marking and quick search, history, view modes, folder views, pointer rules
│  │   ├─ Commands/     CommandId, CommandCatalog, KeyMap (Salamander / Total Commander), menu and panel rules
│  │   ├─ Operations/   FileOperationEngine, transfer scheduling, speed limits, rename/attribute operations
│  │   ├─ Search/       FileSearcher, ContentMatcher, FileWalker, DuplicateFinder
│  │   ├─ Tools/        directory compare and sync, file comparison, multi-rename, NTFS, user menu
│  │   ├─ Viewing/      TextFile (any size), encodings, text/byte search, structured trees, HTML rendering
│  │   ├─ Settings/     AppSettings + Options (settings.json), AppPaths, hot paths, startup arguments
│  │   ├─ Interop/      Win32/Shell P/Invoke (shell operations, links, disk images, MAPI, power, file locks)
│  │   ├─ Formatting/, Localization/, Diagnostics/ (ErrorLog), Updates/ (UpdateCheck)
│  ├─ VibeSalamander.Plugins.Abstractions  the plugin contracts, one file: Contracts.cs
│  ├─ VibeSalamander.Plugins.Archive       built-in: ZIP/TAR/7z/RAR… (SharpCompress + own ZIP writer)
│  ├─ VibeSalamander.Plugins.Checksum      built-in: CRC32 … SHA-512, SFV/MD5/SHA files
│  ├─ VibeSalamander.Plugins.DiskMap       built-in: four views drawn as pixels
│  ├─ VibeSalamander.Plugins.Ftp           built-in: FTP, FTPS (FluentFTP), SFTP (SSH.NET)
│  ├─ VibeSalamander.Plugins.SevenZip      NOT built in: a separate download that drives 7z.exe
│  ├─ VibeSalamander.Launcher              the starter VibeSalamander.exe (net48, runs lib\VibeSalamander.exe)
│  └─ VibeSalamander.App                   WinUI 3 desktop app (unpackaged, self-contained WinAppSDK 1.8; ≈29,000 lines)
│      ├─ MainWindow.*.cs   the window, split by area (menus, toolbar, keyboard, transfers, updates, …)
│      ├─ ViewModels/       MainViewModel.* (commands), PaneViewModel.* (a panel), TransfersViewModel, command classes
│      ├─ Views/            FilePane.* (a panel), AddressBar, ListMenu, dialogs, tool windows, Viewer/
│      ├─ Services/         icons, thumbnails, plugins, shell menu, clipboard, Loc, updater, taskbar
│      ├─ Strings/          <code>.json translations (11 languages besides English)
│      └─ Assets/Editor/    editor.html, diff.html (the pages that host Monaco)
├─ samples/HelloPlugin                     a complete plugin in one file (the wiki's tutorial)
├─ tests/
│  ├─ VibeSalamander.Core.Tests            xUnit: the engine, the rules and the plugins (≈280 tests)
│  └─ VibeSalamander.UiTests               FlaUI (UI Automation): drives the built app (≈70 scenarios)
├─ build/                                  publish.ps1, publish-7zip-plugin.ps1, installer/ (Inno Setup), package-manifests.sh
├─ bucket/, packaging/winget/              Scoop manifest and winget manifests (written by the release workflow)
└─ docs/                                   this file, ROADMAP.md, releases/<version>.md, wiki/ (published to the GitHub wiki)
```

Dependencies point one way: **App → plugins → Abstractions → Core**. Core references no UI and no plugin;
the tests reference Core and the plugins, never the app (the app is tested through its window by the UI tests).

## Main window layout (mirrors Salamander)

```
┌ Title bar (Mica, custom) ──────────────────────────────────────────────┐
│ Menu: Left  Files  Edit  Commands  Plugins  Options  Right  Help       │
│ Top toolbar · hot path bar · user menu bar (each optional)             │
├──────────────── Left panel ───────────┬──────────── Right panel ───────┤
│ Drive bar                             │ Drive bar                      │
│ Address bar  ⌂ This PC › C: › Users › │ Address bar   ★ 🕘 📄 buttons   │
│ Header line     Name Ext Size Date …  │ Header line                    │
│ File list (virtualized ListView)      │ File list / quick view         │
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

Every bar can be toggled (Options → Show), as in Salamander. Errors and messages appear as a notification
(an `InfoBar` over the bottom of the panels) that closes itself. Find, Configuration, Compare, Duplicates,
Multi-rename, the viewer, the editor and plugin windows are separate non-modal windows owned by the main
window (`Services/WindowOwner`).

## Startup

1. `Launcher/Program.cs` (in a release) starts `lib\VibeSalamander.exe` with the same command line.
   `Services/TaskbarIdentity` makes a taskbar pin point back at the starter.
2. `App()` adopts roaming settings into a portable copy's `Data` (`AppPaths.AdoptRoamingSettings`), starts
   `ErrorLog` and hooks the three unhandled-exception events (logged and notified; the program goes on
   where it can).
3. `OnLaunched` loads `settings.json`, loads the language (`Loc.Load`), handles *Allow only one instance*
   (`AppInstance` key `VibeSalamander.Main`; a second start hands over its folders), creates `MainWindow`
   and applies `StartupArguments` (`-L`, `-R`, `-A`, `-P 1|2`).
4. `MainWindow` builds `MainViewModel`, which creates the `PluginManager`, the providers and both
   `PaneViewModel`s; plugins are loaded once here.

## Key design decisions

* **Caret vs. marks.** Salamander separates the *focused item* (caret) from *selected/marked* items
  (Insert/Space/Num+). The ListView's single selection is used only as the caret; marks are a property of
  `FileItemViewModel` (`IMarkable`), changed by Core's `Marking` and drawn in the scheme's "selected" color.
* **Fast listing.** `FileSystemEnumerable<T>` (NtQueryDirectoryFile under the hood) with a transform that
  builds `FileEntry` directly; no `FileInfo` allocations. Listing runs off the UI thread and the result
  replaces the collection in one assignment (no per-item change notifications). `MemoryCleanup` gives
  memory back after a huge listing goes away.
* **Providers.** Every panel path belongs to an `IFileSystemProvider`, found through `FileSystemRegistry`
  (first match wins, in this order; the two plugin-backed ones are put in front with `RegisterFirst`
  because their paths can look like disk paths):

  | Provider | Paths | Where |
  |---|---|---|
  | `RemoteFileSystemProvider` | `sftp://user@host/dir` | App/Services (needs the file system plugins) |
  | `ArchiveFileSystemProvider` | `C:\a\photos.zip\2024` | App/Services (needs the archiver plugins) |
  | `ThisPcProvider` | the drive list (`..` in a drive root goes there) | Core |
  | `FoundListProvider` | Find's / duplicates' "Show in Panel", flat view | Core |
  | `LocalFileSystemProvider` | disk and UNC paths | Core |

  `PaneViewModel` exposes what kind of place it shows (`IsLocalDisk`, `IsArchive`, `IsRemote`, `IsThisPc`,
  `IsFoundList`); commands check these, never the path's text.
* **Commands.** Every action is a `CommandId` (Core). `CommandCatalog` has its name, group and toolbar icon;
  `KeyMap` maps key chords to ids (Salamander or Total Commander defaults plus the user's overrides,
  stored as differences). Menus, toolbar, F-key bar, context menus and shortcuts all send an id to
  `MainViewModel.ExecuteAsync`, which:
  1. lets `ArchivePanelCommands` / `ServerPanelCommands` handle it when the active panel is in an archive
     or on a server (what they do is decided by Core's `PanelCommandRules`);
  2. otherwise runs the handler registered in the `CommandTable` (one handler per id, registered by area in
     `MainViewModel.Commands.cs`; a second handler for an id throws when the table is built);
  3. catches any exception: logged with the command's name, shown as a notification.

  `CanExecute` greys out what doesn't apply. The view model reaches the window only through `IUiHost`
  (dialogs, menus, notifications, focus), which `MainWindow` implements.
* **File operations.** `FileOperationEngine` plans first (tree walk, totals), then executes with CopyFileEx
  (attributes, times and alternate streams kept; long paths; links copied as links, never followed), with
  pause/cancel, conflict and error questions through `IOperationCallbacks`, optional verification, and
  `SpeedLimiter`s. `TransfersViewModel` owns the queue: every copy, move, permanent delete, pack, unpack,
  upload and download is a row in the transfer panel. `TransferScheduler` decides which waiting rows may
  start (`ParallelTransfers`, default 3; one per disk by default). Questions from all transfers go through
  one dialog gate (`UiOperationCallbacks` → `Dialogs`), so prompts never collide. Recycle Bin deletes use
  the shell (`ShellOperations`).
* **Icons and thumbnails.** Glyphs render immediately; `IconCache` loads shell icons on two STA threads of
  its own (SHGetFileInfo is unreliable on thread-pool threads), cached per extension, or per path for
  `.exe/.lnk/.ico` and drives. Icon packs (Windows, Vibe color, Vibe one color) are chosen through Core's
  `FileIcons`. `ThumbnailCache` serves the newest requests first, drops rows that scrolled away and prefers
  what Windows' own thumbnail cache already has.
* **Views.** Brief, Detailed, Icons, Thumbnails, Tiles (`ViewMode`), as one `ListView` with different
  templates and panels (`FilePane.Views.cs`). `ViewStyle.Classic` keeps one view per panel; `Modern`
  remembers a view per folder and picks thumbnails for picture folders (`FolderViews`).
* **Density and theme.** One `RowHeight` setting (Compact 20 px ≈ Salamander, Comfortable 28 px ≈ Files)
  drives list, header and bar sizes. Colors come from a scheme (`ColorOptions.Scheme`; the schemes
  are `UiState.Schemes` in `ViewModels/FileItemViewModel.cs`, the brushes in `Styles/Theme.xaml`; `VsCodeThemes` imports a VS Code theme).
* **List menus.** Drives, hot paths, histories, opened files and the user menu all use `Views/ListMenu`
  (fixed width, 10 rows, type a letter to jump). Use it for any new list of this kind.

## Viewer, editor and compare

* **Viewer (F3)** – `Views/Viewer/ViewerWindow.*`. Core's `ViewerModes` says which modes fit a file. Text
  and hex use `VirtualTextView` over `TextFile` (lines indexed in the background, so any size opens at
  once); JSON/XML/YAML trees come from `Structured`; Markdown, notebooks and HTML are rendered by
  `HtmlRendering` (Markdig) into a WebView2 with scripts off and remote content blocked; pictures, PDFs,
  audio and video use Windows' own decoders.
* **Editor (F4)** – `EditorWindow` hosts **Monaco** in WebView2 through `Assets/Editor/editor.html`.
  Monaco is not in the repository: the first build downloads `monaco-editor` 0.52.2 from npm into
  `Assets\Monaco` (git-ignored; see `VibeSalamander.App.csproj`). Loading, encodings, line endings and
  safe saving are Core's `TextDocuments`. Without the WebView2 runtime the editor falls back to a plain
  text box.
* **Compare Files** – `CompareFilesWindow` with Monaco's diff editor (`diff.html`); binary comparison is
  Core's `FileComparison`.
* Configuration → Viewers / Editors rules by file mask come first; the built-ins can be turned off
  (`ViewerOptions.UseBuiltInViewer` / `UseBuiltInEditor`), then the Windows app opens the file.
* WebView2 keeps its data in `AppPaths.WebView` (a cache, not settings).

## Plugins

`VibeSalamander.Plugins.Abstractions/Contracts.cs` holds every contract. A plugin is a .NET assembly with a
public class implementing `IVibePlugin`; in `Initialize(IPluginHost)` it registers what it adds:

| Salamander plugin type | Vibe contract | Used by |
|---|---|---|
| Archiver | `IArchiver` (`RegisterArchiver`): pack, unpack, list, optionally add/delete in place | Archive, 7-Zip |
| Menu extension | `ICommandContributor` (`RegisterCommands`): Plugins menu and, with `AppliesTo`, the file's context menu | all |
| File system | `IPluginFileSystem` (`RegisterFileSystem`): URL-like paths shown in the panels | FTP / SFTP |
| Windows | `IPluginUi`: `OpenListWindow`, `OpenCanvasWindow`, forms, questions, save dialog, clipboard, `T()` | Checksum, Disk Map, FTP |
| Viewer | none yet (see the roadmap) | – |

* **Loading** (`Services/PluginManager`): built-in plugins are referenced by the app; others are found in
  `<program folder>\Plugins\<name>\` and `%AppData%\VibeSalamander\Plugins\<name>\` (`<name>.dll`, or every
  assembly in the folder), each in its own `AssemblyLoadContext` that shares Core and the contracts with
  the app. Disabled plugins (Configuration → Plugins) are listed but not initialized; a plugin that fails
  to load is listed with the reason. A later-loaded archiver wins for a format it shares with a built-in
  one (that is how 7-Zip takes over `.7z`).
* **Compatibility.** New members of the contracts get default implementations, so plugins built against
  an older host keep loading. There is no declared API version yet beyond `IPluginHost.HostVersion`.
* **Jobs.** Anything long a plugin does runs as an `IArchiveJob` in the transfer panel
  (`TransfersViewModel.EnqueueJob`): progress, pause, cancel, and conflict / error / password questions
  marshalled to the UI. `ProgressTracker` turns byte counts into speed and remaining time.
* **Archive plugin.** Packs ZIP with its own writer (`Zip.cs`: AES-256 / ZipCrypto, ZIP64, split volumes,
  NTFS times, entries copied without unpacking), TAR / TAR.GZ with .NET; unpacks ZIP/7z/RAR with
  SharpCompress and TAR variants with .NET's `TarReader` over gzip / bzip2 / xz / lzip. ZIP and TAR can be
  changed in place (`Archiver.Edit.cs`: written anew beside the old file, swapped in when complete).
  Entry names that would leave the target directory are skipped. Also: test archive, self-extracting ZIP
  (`SelfExtractor`), ZIP comment.
* **Archives as directories** (`Services/ArchiveFileSystemProvider`): `ListAsync` gives the items once per
  archive version (cached by size and time); the provider builds the tree. Opening a file unpacks one item
  to `%TEMP%\VibeSalamander\FromArchives`; saving from the editor puts it back when the archive is editable.
* **FTP plugin.** One listing connection per server and user, a connection of its own for every transfer.
  Saved connections live in the plugin's data directory with passwords protected by Windows DPAPI
  (`ProtectedData`); SFTP checks the server's fingerprint and supports private keys.
* **7-Zip plugin.** Parses 7z.exe's output; pausing suspends the process. Published separately by
  `build/publish-7zip-plugin.ps1` with 7z.exe beside it.

## Settings and data

* One file: `settings.json` (`AppSettings` with nested `*Options` classes, System.Text.Json, enums as
  strings). `SettingsStore.Load` is forgiving: an unreadable value falls back to its default and the rest
  is kept, the damaged file is kept as `settings.broken-….json`, and a locked file is never overwritten.
  **New settings must have a default that keeps old files working**; never rename or repurpose a property.
* `AppPaths` decides where things are:

  | | Installed | Portable (`portable.flag` beside the program) | `VIBE_SALAMANDER_HOME` set |
  |---|---|---|---|
  | settings, `Plugins`, `PluginData` | `%AppData%\VibeSalamander` | `Data\` beside the program | that folder |
  | logs, WebView2 cache | `%LocalAppData%\VibeSalamander` | `Data\` | that folder |

  `installed.flag` (written by the installer) tells the updater to update with the installer.
* **Error log** (`Core/Diagnostics/ErrorLog`): one text file a day, 30 days kept, 5 MB a day at most. It
  never leaves the computer. There is no telemetry and no network call except the update check.

## Languages

`Services/Loc` holds the user interface language (Configuration → General → Language; "" = the Windows
language). Translations are `src/VibeSalamander.App/Strings/<code>.json` files mapping the English text to
the translation (`{ "Copy": "Kopiraj" }`); a missing entry stays English.

* Code uses `Loc.T("text")` / `Loc.F("format {0}", value)` where it builds texts (Core uses
  `Core.Localization.L`, wired to `Loc` at startup; plugins use `IPluginUi.T`).
* XAML, dialogs and menus are translated by walking their elements (`Loc.Translate`): text blocks,
  buttons, headers, placeholders, tooltips, menu items, navigation items. Lists of data (ListView) and
  elements tagged `NoLoc` (names and paths) are skipped.
* Entries with placeholders also translate texts made from them ("Copy {0} to:" matches
  "Copy "a.txt" to:"), when their fixed text is distinctive (6+ letters).
* **Every new user-visible English text needs an entry in all eleven files.**
* Adding a language: copy a `Strings\*.json`, translate the values, add the code to `Loc.Languages`.

## Updates, packaging and releases

* A release folder (`build/publish.ps1`) is the starter plus `lib\` (self-contained, ReadyToRun). The
  installer is Inno Setup (`build/installer/VibeSalamander.iss`, per user or per machine); the portable zip
  is the same folder with `portable.flag`.
* `Core/Updates/UpdateCheck` reads GitHub's latest release and picks the asset for this install kind
  (`InstallKind`: installer, portable zip, Scoop, built from source) and architecture; `Services/Updater` downloads it, checks its SHA-256 when GitHub gives one, and either runs
  the installer silently or replaces a portable copy's files with a small script after exit.
* Workflows (`.github/workflows`), all on pushes to `main` only:

  | Workflow | Does |
  |---|---|
  | `release.yml` | Core tests + x64 build + starter + sample plugin + installer script check. When `<Version>` in `Directory.Build.props` has no GitHub release yet: builds x64 and ARM64, installers, portable zips, the 7-Zip plugin, creates release `v<version>` with `docs/releases/<version>.md` as notes, then commits the Scoop and winget files |
  | `ui-tests.yml` | builds the app, runs the UI tests on a Windows runner, force-pushes screenshots and results to the `ui-screenshots` branch |
  | `wiki.yml` | copies `docs/wiki` to the GitHub wiki (edit the pages here, never on GitHub) |
  | `winget.yml` | submits the release to microsoft/winget-pkgs (needs the `WINGET_TOKEN` secret), retried daily |

* **To release:** raise `<Version>` in `Directory.Build.props`, add `docs/releases/<version>.md`, push to `main`.
* The binaries are not code-signed (SmartScreen warns); see the roadmap.

## Tests

* **Core tests** (`tests/VibeSalamander.Core.Tests`, xUnit) cover the engine on real temporary folders, the
  rules classes, settings robustness, search, compare, viewing, updates, and each plugin (the FTP tests
  cover paths and planning, not a live server).
* **UI tests** (`tests/VibeSalamander.UiTests`, FlaUI/UIA3): `AppSession` starts the built app with its own
  settings folder (`VIBE_SALAMANDER_HOME`) on generated dummy files (`DummyFiles`), sends keys like a user
  and saves a screenshot per step. `DocumentationScreenshots` produces the pictures in `docs/wiki/images`.
  Environment: `VIBE_APP_EXE`, `VIBE_SCREENSHOTS`, `VIBE_HELLO_PLUGIN`.
* Elements the UI tests need are found by name or automation id; give new controls one.
* Building needs Windows (WinUI 3). On another system nothing can be built or run: push and read the
  workflow results and the `ui-screenshots` branch.

## How to add things

| To add | Touch |
|---|---|
| A command | `CommandId` (settings store ids by name: never rename one) → `CommandCatalog` (name, group, toolbar icon) → default key in both `KeyMap.Create*Defaults` if it has one → a handler in `MainViewModel` (`Add*Commands`) → the menu in `MainWindow.Menus.cs` / `MainWindow.xaml` → what it means in archives and on servers (`PanelCommandRules`) → translations → `docs/wiki/Keyboard-Shortcuts.md` |
| A decision with cases | a pure static method in a Core `*Rules` class with tests, called from the view model |
| A setting | a property with a safe default in the right `*Options` class → a control on its page in `SettingsWindow.Pages.cs` (the configuration search picks it up) → translations |
| A dialog | `Views/Dialogs*.cs` (ContentDialog built in code, shown through the one dialog gate); prefer in-place editing or a notification |
| A tool window | a `Window` in `Views/`, owned through `WindowOwner`; `WindowPlacements` remembers where it was |
| A long operation | a job in `TransfersViewModel` (never a modal progress dialog) |
| A file system or archive format | a plugin (`IPluginFileSystem` / `IArchiver`); don't special-case it in the app |
| A plugin capability | a member with a default implementation in `Contracts.cs`, implemented in `PluginManager` / `PluginUi`; document it in `docs/wiki/Writing-Plugins.md` and use it in `samples/HelloPlugin` when it is general |
| A viewer mode | `ViewerModes` (Core) + a `ViewerWindow.<Mode>.cs` partial |
| A language | see Languages |

## Conventions

* Every source file starts with the SPDX header (`GPL-3.0-or-later`).
* Large classes are split into partial files by area, each starting with a one-line comment of what it holds
  (`MainWindow.*.cs`, `MainViewModel.*.cs`, `PaneViewModel.*.cs`, `FilePane.*.cs`, `ViewerWindow.*.cs`).
* Comments say *why* and describe behavior in the user's terms; XML summaries on every type.
* User-visible wording is plain and short, follows Salamander's terms (panel, directory line, hot path,
  user menu) and names the thing it is about; errors say what happened and what can be done.
* Exceptions: catch the specific ones an operation can raise (`IOException`,
  `UnauthorizedAccessException`, …), log with `ErrorLog`, tell the user with a notification. Nothing a
  user does may end the program.
* Commits go to `main`, one user-visible change each, described in one sentence in the user's terms
  ("Unpack: an archive with a single root folder clears …"). A feature comes with its Core tests, a UI
  test scenario when it has UI, translations, and the wiki / README / release notes it changes.
