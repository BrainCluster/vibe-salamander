# Vibe Salamander – Roadmap

Written at **0.8.8** (November 2026), from a review of the whole code base. It has three parts:

1. [Where the project stands](#where-the-project-stands)
2. [The road to 1.0 and after](#the-road-to-10-and-after): what to build next, in order, with where and "done when"
3. [Salamander parity checklist](#salamander-parity-checklist): every Open Salamander command and plugin, and its state

Read [ARCHITECTURE.md](ARCHITECTURE.md) first: it has the structure, the rules and the recipes
("How to add things") this roadmap relies on. Tick items off here in the commit that finishes them.

## Where the project stands

In five weeks Vibe Salamander went from nothing to almost all of Open Salamander's commands, plus things
Salamander never had (a transfer queue, a built-in viewer and editor, compare and synchronize, FTP/SFTP in
the panels, Disk Map, an installer with updates, twelve languages). Feature parity is no longer the gap.

What is missing is what makes people trust a file manager with their files:

* **It is young and unsigned.** Windows warns on every download, no bug has been reported by a user yet
  (the issue tracker is empty), and the copy engine has run on few real machines.
* **A few behaviors can surprise in costly ways**: files that live in the cloud are downloaded by tools
  that only meant to look at them, and a copy into a protected folder just fails.
* **Speed is a principle but isn't measured.** Nothing would notice if a change made listings slower.
* **The plugin contracts are still free to change**, which is fine before 1.0 and must stop at 1.0.

So the order is: **trust first (0.9), then a stable 1.0, then reach (1.x)**. New features that don't serve
the current milestone wait, unless BrainCluster asks for them — his requests always come first.

## The road to 1.0 and after

Each item says what to do, why, where it goes in the code, and when it is done. Within a milestone the
items are in the recommended order. "Verified absent" means a search of the 0.8.8 code found nothing.

### 0.9 — Trust

**T1. Cloud files are never downloaded by accident.**
OneDrive, iCloud and Dropbox keep files as online-only placeholders; reading one downloads it. Find (text),
Find Duplicates, Compare Directories by content, Checksums, Disk Map, quick view and thumbnails read files,
so a search for text in `Documents` can pull down a whole cloud drive. Verified absent: no code looks at the
placeholder attributes.
*Where:* `FileEntry` (keep `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` / `RECALL_ON_OPEN` / `OFFLINE`; they come
with the listing for free), a Core rule "may this tool read this file's content" used by `FileSearcher`,
`DuplicateFinder`, `DirectoryComparer`, the Checksum plugin, `QuickViewPanel` and `ThumbnailCache`;
a cloud state mark in the panel (online-only / available / pinned).
*Done when:* content tools skip online-only files and say how many they skipped, with a choice to include
them; quick view and thumbnails of an online-only file show its icon, never its content; explicit commands
(F3, F5, Enter) work as before; Core tests cover the rule.
*Status (stage 2):* `Core/FileSystem/CloudFiles` (recall-on-data-access or offline = online-only). Find with text
and inside ZIP files, Find Duplicates by content and Compare Directories by content (marks and the list) skip such
files, count them in their status, and have a box to include them; quick view says the file is only in the cloud;
thumbnails come only from Windows' thumbnail cache; .exe / .lnk / .ico icons are the type's; Checksums reads a
file the user marked but not the cloud-only files inside a marked folder. The Attr column shows O / P / U. Still
open: a cloud icon in the panel rows (the Attr letters are the only mark so far).

**T2. A data-safety test matrix for the copy engine.**
`FileOperationEngine` is the code that can lose files. It has edge-case tests; it needs a systematic matrix
for the dangerous combinations.
*Where:* `tests/VibeSalamander.Core.Tests` (new file), fixes in `Core/Operations`.
*Cases:* a move across volumes that fails or is cancelled midway (nothing deleted from the source that did
not arrive; no partial target left), every conflict answer × file/directory × "for all", the target inside
the source, the same file as source and target, names differing only in case, read-only / hidden / system
targets, files locked by another program, paths over 260 characters on both sides, junctions and symbolic
links (their content copied, a move never deletes a link's target), alternate data streams,
a full target disk, the source disappearing during the copy, pause and resume inside a large file,
verification failing.
*Done when:* each case is a test; every failure found is fixed or, if it is a decision, written down here.
*Status (stage 1):* `FileOperationSafetyTests` covers the matrix except a full disk (needs a small volume of its
own). Found and fixed: a target that is the source itself through a junction, hard link or subst drive was offered
as an ordinary overwrite (now recognized by file identity and refused); a folder copied into a link that leads
inside it was copied into itself; a copy that failed verification stayed in the target and Retry could never
succeed. **Decided (BrainCluster, 2026-11-16):** an overwrite keeps writing over the old file in place, as Explorer and
Salamander do; cancelling it or a failure midway loses the old file. No temporary-name-and-swap.

**T3. Administrator rights for single operations.**
Copying into `Program Files` or deleting in `Windows` fails with "access denied" (verified: only
*Run as administrator* for programs exists). Explorer offers to continue as administrator.
*Where:* a small elevated helper process started on demand with the `runas` verb, which runs the same
`FileOperationEngine` for the failed items and reports progress over a pipe; the question goes through
`IOperationCallbacks` like other errors ("Continue as administrator / Skip / Cancel", with "for all").
*Done when:* copy, move, delete, rename, new folder and attribute changes in protected folders succeed after
one Windows prompt per transfer; the main program never runs elevated; declining leaves a normal error.
*Status (stage 5):* copy, move and permanent delete (with their new folders, times and attributes) go through
`Core/Operations/Elevation` and `lib\VibeSalamander.Elevated.exe` (see ARCHITECTURE, "Administrator rights for
one transfer"); `ElevationTests` cover the engine flow, the helper's protocol and the real helper program.
Left: in-place rename (F2), F7's new folder and the attributes dialog, which don't run in the engine; and a test
of the Windows prompt itself, which the build machines (already administrator) can't show.

**T4. Measured speed.**
*Where:* a benchmark set in the Core tests (list 100,000 files, sort them, quick search, plan a copy of a
deep tree, open a 2 GB text file, search content of 10,000 files), with generous upper limits as tests so
a tenfold regression fails the build; a UI test that records startup-to-first-listing time and memory
after showing a 50,000-file folder, printed in the `ui-screenshots` README.
*Done when:* the numbers are in every CI run and a line in the release notes template asks to compare them.
*Status (0.8.9, sizes reduced after it):* `PerformanceTests` (Core): listing and sorting 20,000 files, finding
names and text, duplicates, copying a tree, opening and indexing a 64 MB text file, each with a generous limit; the
times are in the summary of every *Build and release* run. `MeasurementScenarios` (UI): start time, the End key in
a folder of 20,000 files and memory, on the `ui-screenshots` page. Smaller than first planned (100,000 files, 2 GB)
to keep the runs short. Open: the line in the release notes.

**T5. Translations checked by a test.**
The eleven language files have between 1,707 and 1,714 entries: some are missing texts. Verified absent:
no test compares them.
*Where:* a Core test that reads `src/VibeSalamander.App/Strings/*.json`: every file has the same keys;
placeholders (`{0}`) match between key and value; every literal passed to `Loc.T` / `Loc.F` / `L.T` and
every text in XAML that `Loc.Translate` would translate has a key (a source scan with a small allow-list).
*Done when:* the test passes and fails for a text added without translations.

**T6. Accessibility.**
Only a handful of controls have automation names; a keyboard-first program should be fully usable with a
screen reader and in high contrast.
*Where:* `AutomationProperties.Name` on every button, bar and list that has only an icon; the panel rows
announce name, size, date and "marked"; live regions for notifications and transfer completion; every
window reachable and closable by keyboard; high-contrast themes checked in each color scheme.
*Done when:* a UI test walks every window with UI Automation and finds no unnamed interactive element,
and Narrator reads a panel row sensibly (BrainCluster checks by ear).

**T7. Code signing.** *(needs BrainCluster: an account and a decision)*
SmartScreen's warning is the first thing every new user sees, and the updater installs unsigned files.
*Options to evaluate:* a free open-source signing program such as SignPath Foundation, Azure's Trusted
Signing, or a bought certificate. *Where:* a signing step in `release.yml` for the program, the starter,
the installer and the 7-Zip plugin; `Updater` then verifies the downloaded file's signature and publisher,
not only its SHA-256.
*Done when:* a release installs without the warning's "unknown publisher" and the updater refuses a file
that isn't signed by the same publisher.

**T8. Flaky UI tests are fixed, not retried.**
The history shows waits being lengthened for slow test machines. Replace fixed sleeps in
`tests/VibeSalamander.UiTests` with waits for a state the app exposes, so a red UI run always means a real problem.
*Status (0.8.9):* the main window has an element that isn't displayed, `AppState`: "ready", or "busy: panel"
(listing, calculating sizes), "busy: editor" / "busy: compare files" (until Monaco has the text), "busy: viewer"
(`Services/AppActivity`). The tests wait for "ready" at the start, after every key and click (on top of the short
pause that was there), and instead of the 6-second guesses for the editor. Open: the remaining fixed pauses are
before screenshots and around the mouse; plugin windows (Disk Map, Checksums) and dialogs don't report their
state yet.

### 1.0 — Stable

**S1. Freeze the plugin contracts.**
*Where:* `Contracts.cs`: review every interface once for names and gaps (below), add
`IVibePlugin.MinimumHostVersion` (default: any) and refuse a plugin that needs a newer host with a clear
reason in the Plugins Manager; a test project that compiles `samples/HelloPlugin` against the published
contracts of the previous release. From 1.0 on, contracts only grow, by members with defaults.
*Gaps to close before the freeze:*
- a **viewer contract** (`IPluginViewer`: masks it handles, a window through `IPluginUi`, or a picture it
  draws) so formats can be added without touching the app;
- **panel columns and an information line from a file system plugin** (permissions and owner on SFTP);
- **per-plugin settings page** in Configuration → Plugins (today: forms the plugin opens itself);
- `IPluginFileSystem`: free space, change permissions, symbolic link targets, resume of a broken transfer.

**S2. The last parity items** (each small; see the checklist for details):
Convert (Ctrl+K: line endings and encodings; `TextDocuments` already does the work), Plugins → Last
command (Ctrl+I), Copy UNC path as text (Ctrl+Shift+Ins), Edit with (Ctrl+Shift+F4), show/hide the directory
line, rename and new folder inside ZIP/TAR archives, icon overlays (the shell's overlay index from
`SHGetFileInfo`, drawn over the icon in `IconCache`; this is also how OneDrive and Git clients show state).

**S3. Windows integration** (verified absent; an installer task each, off by default):
"Open in Vibe Salamander" in Explorer's folder menu; `vibesalamander <folder>` on the PATH; optionally
Win+E / the default folder handler. *Where:* `build/installer/VibeSalamander.iss` (registry, removed on
uninstall), `StartupArguments` already takes folders.

**S4. Undo for the last operation.**
Ctrl+Z after a wrong F6 or a slip of a drag: undo the last rename, move, copy (delete the copies, asked
first) or Recycle Bin delete, as Explorer does. Salamander doesn't have it; people who come from Explorer
miss it, and it fits "trust". *Where:* a journal of finished operations in Core (`Operations/`), one new
`CommandId.Undo`; only operations whose result is unchanged are offered.

**S5. 1.0 housekeeping.**
The README's "still young" note reviewed, a `CONTRIBUTING.md` (build, tests, the commit style, how
translations work), a security policy (how to report a vulnerability), issue templates checked, the wiki
complete for every command, the first-run page (*First Steps*) shown once after an update with what's new.

### 1.x — Reach

In rough order of how many people each one serves. Each is a plugin unless noted, so 1.0's contracts must
be able to carry them (that is the test of S1).

| | What | Notes |
|---|---|---|
| R1 | **Phones and cameras (MTP)** | Windows Portable Devices as an `IPluginFileSystem` (`mtp://device/…`), shown in This PC and the drive menu. The most common reason people open Explorer next to a file manager. Verified absent. |
| R2 | **Network** | Browse computers and their shares (`\\server` lists its shares; a Network item in This PC), remembered credentials through Windows' own dialog. Salamander's Network plugin. Today only mapped drives and typed UNC paths work. |
| R3 | **More servers** | WebDAV and S3-compatible storage as file system plugins; for FTP/SFTP: resume, compare and synchronize a local directory with a server (reuse `DirectorySync`), keep-alive, a transfer log. |
| R4 | **Tabs in each panel** | *Decision needed (below).* Folder tabs per panel as Total Commander has them; Salamander never did. |
| R5 | **Instant name search** | Use the Windows Search index (or Everything, when installed) for name searches in indexed places, falling back to walking the disk. In `FileSearcher`, behind an option. |
| R6 | **Split and combine files; encrypt, decrypt, shred** | Salamander's File Splitter and Encrypt plugins, as one "File tools" plugin. |
| R7 | **Master password** | Optional password for saved server passwords (today protected by the Windows account through DPAPI), as in Salamander's Security page. |
| R8 | **More formats in the viewer** | Through the viewer contract of S1: databases (DBF, CSV as a table, SQLite), fonts, Office documents' text. |
| R9 | **Workspaces** | Save and restore both panels' folders, views and sort as a named layout, with a shortcut. |

### Not planned

* **Anything but two panels** as the main layout (a folder tree pane, a single-pane mode).
* **Registry editor, Undelete, Windows Mobile, UnCHM, Automation scripting** (Salamander plugins): other
  tools do these better today; the user menu covers scripting. A third party can write them as plugins.
* **A Store (MSIX) package**: the app is deliberately unpackaged (shell integration, plugins in a folder,
  portable mode). Revisit only if signing through the Store becomes the only practical route.
* **Telemetry.** The error log stays on the user's computer.

### Decisions for BrainCluster

1. **Signing (T7):** which route, and under whose name the publisher appears.
2. **Tabs (R4):** in keeping with "what made Salamander great", or a step toward Total Commander? If yes, it
   is large and touches `PaneViewModel`, settings and the keyboard; better decided before 1.0 even if
   built after.
3. **Undo (S4):** wanted for 1.0, or later?
4. **1.0 date:** the plan above is roughly 0.9 = T1–T8, 1.0 = S1–S5. Anything that must be in 1.0 that
   isn't listed?

### Working rules (for whoever builds this, human or model)

* One user-visible change per commit, on `main`, described in one sentence in the user's terms.
* A feature is finished when it has: Core tests for its rules, a UI test scenario if it has UI,
  translations in all eleven files, the wiki / README text, and a line in the next `docs/releases/<version>.md`.
* Keep decisions in Core `*Rules` classes; keep long work in the transfer panel; never add a modal
  progress dialog; never make a plugin touch WinUI.
* Settings only grow: new properties with safe defaults, nothing renamed. `CommandId` names are stored in
  users' settings: never rename one.
* The app can only be built and run on Windows. Without it, push a branch, start the workflows on it and read
  their results before calling something done; say plainly what was not run.
* Tests: for a feature or fix run only its own tests (`-f tests=<its test classes>`); run everything
  (`-f tests=all`, both workflows) only before a release. See ARCHITECTURE.md, *Which tests run when*.
* When Salamander's behavior is the question, read Open Salamander's source rather than guessing.
* Release when a milestone item or a batch of fixes is worth having: the full test run, then raise `<Version>`,
  write the notes, push.

---

## Salamander parity checklist

Generated from Open Salamander's main menu (`src/menu4.cpp`) and hotkeys, corrected against the 0.8.8
code. `[x]` = implemented, `[~]` = partly (what's missing is named), `[ ]` = not yet (roadmap item in
brackets).

### History: the phases that built it

1. **Shell & navigation** – window layout, dual panes, drive bars, This PC, listing, sorting, caret/marks,
   keyboard navigation, settings. *Done.*
2. **Operations engine** – own copy/move/delete engine, in-place rename, attributes/case, find, filter,
   selection, clipboard, drag & drop, Explorer context menu. *Done.*
   *Decision:* no modal progress dialog; everything runs in the **transfer panel**.
   *Superseded decision:* "no internal viewer" — since 0.6 there is a built-in viewer (F3) and editor (F4);
   the Windows app is still one setting away.
3. **Power features** – configuration, key customization, views, columns, hot paths, user menu, compare
   directories, sizes, drive information, command line, network drives. *Done.*
4. **Plugins** – loader and manager, archives (pack, unpack, browse, change in place), FTP/SFTP, checksums,
   Disk Map, 7-Zip. *Done; the remaining plugin ports are in the table at the end.*

### Left / Right panel menu
| Command | Keys | Done |
|---|---|---|
| Change drive | Alt+F1 / Alt+F2 | [x] |
| Back / Forward | Alt+Left / Alt+Right | [x] |
| Parent directory | Backspace | [x] |
| Root directory | Shift+Backspace | [x] |
| Path from other panel | Ctrl+. | [x] |
| Hot paths 1-9 (set / go; own button and menu, Shift+F9; more hot paths with shortcuts of their own; no 0: Windows uses Ctrl+Shift+0) | Ctrl+Shift+1-9 / Ctrl+1-9 | [x] |
| Sort by Name/Ext/Size/Date/Attr | Ctrl+F3…F6 | [x] |
| Views (Brief, Detailed, Icons, Thumbnails, Tiles; Classic or Modern folder views) | Alt+1…5 | [x] |
| Show directory / header / information line | | [~] header and information line; the directory line can't be hidden (S2) |
| Choose columns (header right-click), resize columns (header grips) | | [x] |
| Maximize/restore panel | Ctrl+F11 | [x] |
| Swap panels | Ctrl+U | [x] |
| Filter | Ctrl+F12 | [x] |
| Refresh | Ctrl+F9 / Ctrl+R | [x] |

### Files menu
| Command | Keys | Done |
|---|---|---|
| Open / execute | Enter | [x] |
| Quick rename (in place) | F2 | [x] |
| View (built-in viewer; per-mask viewers; Windows app) / Open with | F3 / Alt+F3, Ctrl+Shift+F3 | [x] |
| Edit (built-in editor; per-mask editors) / Edit new file | F4 / Shift+F4 | [x] |
| Edit with | Ctrl+Shift+F4 | [ ] (S2) |
| Copy / Move-rename (target dialog with Browse and "don't show again") | F5 / F6 | [x] |
| Delete (Recycle Bin / permanent) | F8, Del / Shift+Del | [x] |
| Email (Simple MAPI) | Ctrl+E | [x] |
| Properties | Alt+Enter | [x] |
| Security | Ctrl+P | [x] |
| Change attributes | Ctrl+F2 | [x] |
| NTFS compress/uncompress/encrypt/decrypt | | [x] |
| Change case | Ctrl+F7 | [x] |
| Convert (line endings, encodings) | Ctrl+K | [ ] (S2) |
| Pack / Unpack (in the transfer panel, with passwords) | Alt+F5 / Alt+F6 | [x] |
| New (shell "New" menu; the new item is renamed in place) | | [x] |
| Run as administrator (new) | | [x] |
| Exit | Alt+F4 | [x] |

### Edit menu
| Command | Keys | Done |
|---|---|---|
| Cut / Copy / Paste (clipboard) | Ctrl+X / Ctrl+C / Ctrl+V | [x] |
| Paste shortcut; Copy as shortcut (new) | Ctrl+S; Ctrl+Shift+L | [x] |
| Copy full name / name / path as text | Alt+Ins, Alt+Shift+Ins, Ctrl+Alt+Ins | [x] |
| Copy UNC path as text | Ctrl+Shift+Ins | [ ] (S2) |
| Mark / unmark item | Insert, Space | [x] |
| Select… / Unselect… (mask) | Num + / Num − | [x] |
| Invert selection | Num * | [x] |
| Select all / Unselect all | Ctrl+A / Ctrl+D | [x] |
| Restore selection | Ctrl+W | [x] |
| Save / load selection | Ctrl+Shift+F5 / F6 | [x] |
| Select/unselect same extension / name | Shift+Num± / Ctrl+Shift+Num± | [x] |
| Hide selected / unselected names, show hidden names | | [x] |
| Go to previous / next selected | Alt+Up / Alt+Down | [x] |

### Commands menu
| Command | Keys | Done |
|---|---|---|
| Create directory | F7 | [x] |
| Change directory | Shift+F7 | [x] |
| Compare directories (and synchronize: new) | Ctrl+F10 | [x] |
| Calculate occupied space / directory sizes | Alt+F10 / Ctrl+Shift+F10 | [x] |
| Drive information (flyout) | Ctrl+F1 | [x] |
| Find files and directories (text rows, regular expressions, hex, inside ZIP files) | Alt+F7 | [x] |
| Make file list (to the clipboard) | Ctrl+M | [x] |
| Go to shortcut target | Ctrl+T | [x] |
| Opened files / working directories lists | Alt+F11 / Alt+F12 | [x] |
| Connect / disconnect network drive, shares | F11 / F12 / Ctrl+Shift+F9 | [x] |
| Command shell | Num / | [x] |
| User menu (with editor and $(variables)) | F9 | [x] |
| Open folder (Desktop, Computer, Control Panel, Recycle Bin …) | | [x] |
| Open active folder in Explorer | Shift+F3 | [x] |

### Plugins menu
| Command | Done |
|---|---|
| Plugins manager (Configuration → Plugins: list, turn on/off, plugins folder) | [x] |
| One submenu per plugin with its commands; plugin commands in a file's context menu | [x] |
| Archives: pack ZIP / TAR / TAR.GZ (7z with the 7-Zip plugin), unpack ZIP, 7z, RAR, TAR (+gz/bz2/xz/lz) | [x] |
| Archives: test, self-extracting archive (ZIP → .exe), ZIP comment, split volumes | [x] |
| Browse archives like directories; copy out; open and edit a file inside | [x] |
| Change archives in place: copy into, delete, move out (ZIP, TAR; 7z with the 7-Zip plugin) | [~] rename and new folder inside (S2) |
| Checksums: calculate and verify CRC32 / MD5 / SHA-1 / SHA-256 / SHA-512 | [x] |
| Last command (Ctrl+I) | [ ] (S2) |

### Options menu
| Command | Done |
|---|---|
| Configuration window with Open Salamander's pages, plus search, Plugins, Backup and About | [x] |
| Confirmations (delete, overwrite, hidden/system, merge, NTFS, drag & drop, exit with running transfers, …) | [x] |
| Recycle Bin: never / always / by file masks | [x] |
| Panel options (casing, size format, sorting, typing to the command line, right-click marks, drag threshold, …) | [x] |
| Appearance: single-click open, dim hidden items, panel font, information line format, thumbnail size, icon packs | [x] |
| Colors: schemes, file type colors, highlighting by mask and attributes, VS Code theme import | [x] |
| Main window title; single instance; beep when background transfers finish | [x] |
| Drives: auto-refresh and simple icons per drive type; Change Drive menu (hidden drives, extra items) | [x] |
| Viewers / editors by file mask; history options and command line history | [x] |
| Languages (English and eleven translations) | [x] |
| Key customization (any command, conflicts moved, Salamander or Total Commander defaults) | [x] |
| Customize toolbar; hot path bar and user menu bar; show/hide bars | [x] |
| Export / import / save / reset configuration | [x] |
| Always on top; density; theme | [x] |
| Security / master password | [ ] (R7) |
| Icon overlays | [ ] (S2) |

### Panel behavior
| Behavior | Done |
|---|---|
| `..` up-dir; in a drive root it leads to This PC | [x] |
| This PC: drives with label, type, total, free, fullness bar | [x] |
| Quick search by typing (Salamander style) | [x] |
| Tab switches panels; Ctrl+Left/Right open in other panel | [x] |
| Auto-refresh on file system changes (keeps caret and marks) | [x] |
| Hidden/system files toggle (Ctrl+H) | [x] |
| Real shell icons | [x] |
| Icon overlays | [ ] (S2) |
| Drag & drop between panels and to/from Explorer; rectangle selection | [x] |
| Live drives (plugged in / pulled out) | [x] |
| Context menu: Vibe Salamander's, with the Windows menu at the bottom (Shift+right-click = Windows menu) | [x] |
| Disk images: Enter mounts .iso / .img / .vhd(x); Unmount in the context menu | [x] |
| Thumbnails | [x] |
| Cloud files shown with their state and never downloaded by accident | [~] the panel mark is the Attr column's O / P / U; a cloud icon in the rows is open (T1) |

### Open Salamander's plugins

The left column was written from memory of Open Salamander's `src/plugins` folder, not generated from it:
check it against that folder before relying on it for a "nothing is missing" claim.

| Salamander plugin | In Vibe Salamander | State |
|---|---|---|
| ZIP, TAR, UnRAR, 7-Zip, UnCAB, UnARJ, UnLHA, UnISO, UnFAT, PAK | Archive plugin (built in) + 7-Zip plugin (the rarer formats through 7z.exe); disk images mount as drives | [x] |
| Zip2SFX | Archive plugin: Make self-extracting archive | [x] |
| FTP Client, WinSCP | FTP / SFTP plugin (built in) | [x] resume and synchronize: R3 |
| Checksum | Checksum plugin (built in) | [x] |
| DiskMap | Disk Map plugin (built in), four views | [x] |
| PictView, Internal Viewer, IEViewer | the built-in viewer (pictures, text/hex, Markdown / HTML) | [x] |
| File Comparator | Compare Files (in the app) | [x] |
| Renamer | Multi-rename (in the app) | [x] |
| Check Version | Help → Check for Updates | [x] |
| Network | mapped drives and typed UNC paths only | [ ] (R2) |
| File Splitter, Encrypt | | [ ] (R6) |
| Database Viewer | | [ ] (R8) |
| UnMIME, UnCHM | | [ ] through the viewer contract, if asked for |
| Automation, Registry Editor, Undelete, Windows Mobile | | not planned |

### Beyond Salamander (already there)

Transfer panel with queue, one transfer per disk, speed limits and graphs, verification, *when done*;
built-in viewer for text of any size, trees, Markdown, notebooks, pictures, PDF, audio and video; Monaco
editor with checking and live preview; Compare Files; synchronize directories; Find Duplicates; quick view;
flat view; multi-rename; duplicate; Total Commander key scheme; Windows 11 address bar and list menus;
Modern folder views; color schemes and VS Code theme import; icon packs; installer, portable mode and
self-update; Scoop and winget; error log; UI tests that drive the real app.
