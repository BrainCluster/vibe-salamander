# Vibe Salamander – Roadmap and Salamander parity checklist

The checklist below is generated from Open Salamander's main menu (`src/menu4.cpp`) and hotkeys.
Phase numbers say when each item is planned. `[x]` = implemented, `[~]` = partly, `[ ]` = not yet.

## Phases

1. **Shell & navigation** – window layout, dual panes, drive bars, This PC, listing, sorting,
   caret/marks, keyboard navigation, basic file operations through the Windows shell, settings.
2. **Operations engine** – own copy/move/delete engine with Salamander progress & conflict dialogs,
   in-place quick rename, change attributes/case, find files (Alt+F7), filter, selection dialogs,
   clipboard, drag & drop, Explorer context menu.
   *Decision:* no internal viewer. F3 opens files in their default Windows app, Alt+F3 / Ctrl+Shift+F3
   show the Windows "Open with" chooser.
   *Decision:* no modal progress dialog. Copy, move and permanent delete run in a **transfer panel**
   below the file panels (WinSCP-style queue, up to 3 at once, pause/resume/cancel per item); the
   panels stay usable while transfers run. Conflict and error questions name the transfer they belong to.
3. **Power features** – configuration dialog, key customization, view modes (Brief/Detailed/Icons/
   Thumbnails/Tiles), column chooser & resizing, hot paths, user menu, compare directories,
   directory sizes, drive information, command line integration, network drives.
4. **Plugins** – plugin loader and Plugins Manager (done), archives: pack ZIP/TAR/TAR.GZ, unpack
   ZIP/7z/RAR/TAR/gz/bz2/xz/lz, browse archives in the panels and copy files out (done); later:
   changing archives in place (done), FTP/SFTP (first version done),
   picture viewer, checksum, renamer, file compare, disk map … (ports of Salamander plugins).

## Left / Right panel menu
| Command | Keys | Phase | Done |
|---|---|---|---|
| Change drive | Alt+F1 / Alt+F2 | 1 | [x] |
| Back / Forward | Alt+Left / Alt+Right | 1 | [x] |
| Parent directory | Backspace | 1 | [x] |
| Root directory | Shift+Backspace | 1 | [x] |
| Path from other panel | Ctrl+. | 1 | [x] |
| Hot paths 1-9 (set / go; listed in the drive menu; no 0: Windows uses Ctrl+Shift+0) | Ctrl+Shift+1-9 / Ctrl+1-9 | 3 | [x] |
| Sort by Name/Ext/Size/Date/Attr | Ctrl+F3…F6 | 1 | [x] |
| Views (Brief, Detailed, Icons, Thumbnails, Tiles) | Alt+1…5 | 3 | [x] |
| Show directory/header/information line | | 1 | [~] |
| Choose columns (header right-click), resize columns (header grips) | | 3 | [x] |
| Maximize/restore panel | Ctrl+F11 | 1 | [x] |
| Swap panels | Ctrl+U | 1 | [x] |
| Filter | Ctrl+F12 | 2 | [x] |
| Refresh | Ctrl+F9 / Ctrl+R | 1 | [x] |

## Files menu
| Command | Keys | Phase | Done |
|---|---|---|---|
| Open / execute | Enter | 1 | [x] |
| Quick rename (in place, the name label itself becomes editable) | F2 | 2 | [x] |
| Open in default app / Open with (no internal viewer) | F3 / Alt+F3, Ctrl+Shift+F3 | 2 | [x] |
| Edit / Edit with / Edit new file | F4 / Ctrl+Shift+F4 / Shift+F4 | 1 (system editor) | [~] |
| Copy / Move-rename | F5 / F6 | 1 (shell) / 2 (engine) | [x] |
| Copy/move target dialog with Browse and "don't show again" (then straight into the other panel) | F5 / F6 | 2 | [x] |
| Delete (Recycle Bin / permanent) | F8, Del / Shift+Del | 1 | [x] |
| Email (Simple MAPI) | Ctrl+E | 3 | [x] |
| Properties | Alt+Enter | 1 | [x] |
| Security | Ctrl+P | 3 | [x] |
| Change attributes | Ctrl+F2 | 2 | [x] |
| NTFS compress/uncompress/encrypt/decrypt | | 3 | [x] |
| Change case | Ctrl+F7 | 2 | [x] |
| Convert | Ctrl+K | 4 | [ ] |
| Pack / Unpack (archive plugin; in the transfer panel, with passwords for ZIP/7z/RAR) | Alt+F5 / Alt+F6 | 4 | [x] |
| New (shell "New" menu; the new item is renamed in place) | | 3 | [x] |
| Exit | Alt+F4 | 1 | [x] |

## Edit menu
| Command | Keys | Phase | Done |
|---|---|---|---|
| Cut / Copy / Paste (clipboard) | Ctrl+X / Ctrl+C / Ctrl+V | 2 | [x] |
| Paste shortcut | Ctrl+S | 3 | [x] |
| Copy full name / name / path / UNC path as text | Alt+Ins, Alt+Shift+Ins, Ctrl+Alt+Ins, Ctrl+Shift+Ins | 1 | [~] |
| Mark / unmark item | Insert, Space | 1 | [x] |
| Select… / Unselect… (mask) | Num + / Num − | 2 | [x] |
| Invert selection | Num * | 1 | [x] |
| Select all / Unselect all | Ctrl+A / Ctrl+D | 1 | [x] |
| Restore selection | Ctrl+W | 2 | [x] |
| Save / load selection | Ctrl+Shift+F5 / F6 | 3 | [x] |
| Select/unselect same extension / name | Shift+Num± / Ctrl+Shift+Num± | 2 | [x] |
| Hide selected / unselected names, show hidden names | | 3 | [x] |
| Go to previous / next selected | Alt+Up / Alt+Down | 2 | [x] |

## Commands menu
| Command | Keys | Phase | Done |
|---|---|---|---|
| Create directory | F7 | 1 | [x] |
| Change directory | Shift+F7 | 1 | [x] |
| Compare directories | Ctrl+F10 | 3 | [x] |
| Calculate occupied space / directory sizes | Alt+F10 / Ctrl+Shift+F10 | 3 | [x] |
| Drive information (flyout) | Ctrl+F1 | 3 | [x] |
| Find files and directories | Alt+F7 | 2 | [x] |
| Make file list (to the clipboard) | Ctrl+M | 3 | [x] |
| Go to shortcut target | Ctrl+T | 2 | [x] |
| Opened files / working directories lists | Alt+F11 / Alt+F12 | 3 | [x] |
| Connect / disconnect network drive, shares | F11 / F12 / Ctrl+Shift+F9 | 3 | [x] |
| Command shell | Num / | 1 | [x] |
| User menu (with editor and $(variables)) | F9 | 3 | [x] |
| Open folder (Desktop, Computer, Control Panel, Recycle Bin …) | | 3 | [x] |
| Open active folder in Explorer | Shift+F3 | 1 | [x] |

## Plugins menu
| Command | Phase | Done |
|---|---|---|
| Plugins manager (Configuration → Plugins: list, turn on/off, plugins folder) | 4 | [x] |
| One submenu per plugin with its commands | 4 | [x] |
| Archive plugin: pack ZIP / TAR / TAR.GZ, unpack ZIP, 7z, RAR, TAR (+gz/bz2/xz/lz), gz, bz2, xz, lz | 4 | [x] |
| Browse archives like directories (Enter on an archive; address bar, chevrons, ".." back out) | 4 | [x] |
| Copy files and directories out of an archive (F5 / Alt+F6, into the transfer panel) | 4 | [x] |
| Open a file in an archive (Enter / F3: temporary copy, opened with its program) | 4 | [x] |
| Checksum plugin: calculate CRC32 / MD5 / SHA-1 / SHA-256 / SHA-512 (non-blocking window, copy to clipboard, save SFV / MD5 / SHA files), verify checksum files (Ctrl+Shift+V, Focus), configuration | 4 | [x] |
| Change archives in place (copy into, delete, rename inside) | 4 | [ ] |
| Last command (Ctrl+I) | 4 | [ ] |

## Options menu
| Command | Phase | Done |
|---|---|---|
| Configuration window (General, Recycle Bin, Confirmations, Panels, Views, Appearance, Colors, Main Window, Drives, Change Drive Menu, Viewers, Editors, History, Keyboard, Toolbar, Hot Paths, User Menu, Backup — as in Open Salamander) | 3 | [x] |
| Confirmations: permanent delete, non-empty directory delete, overwrite, hidden/system delete and overwrite, directory merge, NTFS compress/encrypt, drag & drop; messages for Find close/stop, create target path, Always on Top, exit, exit with running transfers, email, create missing directory | 3 | [x] |
| Recycle Bin: never / always / by file masks | 3 | [x] |
| Panel options: select includes directories, name casing, size format, typing to command line, Shift+digit hot paths, right-click marks, slow-click rename, drag threshold, regional / numerical / newest-first sorting, directories with extensions | 3 | [x] |
| Appearance: single-click open, dim hidden items, panel font and size, information line format, thumbnail size, maximize button in address bar | 3 | [x] |
| Colors: schemes (Vibe, Explorer, terminal-inspired Yazi / Superfile / Tokyo Night / Gruvbox / Dracula / Nord / Phosphor, Norton Commander, DOS Navigator, Custom) with file type colors, cursor-bar text color, live preview; file highlighting by mask and attributes | 3 | [x] |
| Main window title (full path / directory / custom prefix); single instance; beep when background transfers finish | 3 | [x] |
| Drives: auto-refresh and simple icons per drive type, refresh on activation, fallback for inaccessible directories; Change Drive menu (hidden drives, separators, extra items) | 3 | [x] |
| Viewers / editors by file mask (default Windows app otherwise); history options and command line history | 3 | [x] |
| Languages: English, Hrvatski, Čeština, Deutsch, Español, Français, Italiano, Nederlands, Polski, Português (Brasil), Русский, Українська (Configuration → General → Language, restart to apply) | 4 | [x] |
| Security / master password, icon overlays | 4 | [ ] |
| Show/hide bars (toolbars, drive bars, command line, F-key bar, transfer panel) | 1 | [x] |
| F-key bar follows held Ctrl / Alt / Shift (F1–F12) | 2 | [x] |
| Ask for copy/move target (on/off, Configuration → Confirmations) | 2 | [x] |
| Fewer dialogs: F7 / Shift+F4 create "New Folder" / "New Text Document.txt" and rename in place; Shift+F7 edits the address bar; Recycle Bin delete without confirmation (Options toggle); errors as self-closing notifications; transfer failures shown in the transfer row (new) | 2 | [x] |
| Transfer panel: Clear finished / Clear all | 2 | [x] |
| Address bar in Windows 11 style: This PC › drive › folders, clickable segments, subfolder chevrons, click empty area to type (new) | 2 | [x] |
| Transfer panel: background queue, parallel transfers, pause/resume/cancel (new, WinSCP-style) | 2 | [x] |
| Customize toolbar; hot path bar and user menu bar | 3 | [x] |
| Always on top | 1 | [x] |
| Export / import / save / reset configuration | 3 | [x] |
| Key customization (any command, conflicts moved, reset to Salamander defaults) | 3 | [x] |
| Density: Compact / Comfortable (new) | 1 | [x] |
| Theme: system / light / dark (new) | 1 | [x] |

## Panel behavior
| Behavior | Phase | Done |
|---|---|---|
| `..` up-dir; in a drive root it leads to This PC | 1 | [x] |
| This PC: drives with label, type, total, free, fullness bar | 1 | [x] |
| Quick search by typing (Salamander style) | 1 | [x] |
| Tab switches panels; Ctrl+Left/Right open in other panel | 1 | [x] |
| Auto-refresh on file system changes (keeps caret and marks) | 1 | [x] |
| Hidden/system files toggle (Ctrl+H) | 1 | [x] |
| Real shell icons, overlays | 1 (icons) / 3 (overlays) | [~] |
| Drag & drop between panels and to/from Explorer | 2 | [x] |
| Mouse rectangle selection from empty space (Ctrl toggles), with auto-scroll (new) | 3 | [x] |
| Live drives: a plugged-in USB drive or memory card opens in the focused panel; panels leave a drive that is pulled out (This PC); drive bars update (new) | 4 | [x] |
| Context menu (shell) | 2 | [x] |
| Vibe Salamander context menu (common commands, View / Sort by, Windows menu at the bottom; Shift+right-click = Windows menu) | 4 | [x] |
| Disk images: Enter mounts .iso / .img / .vhd(x) and opens the drive in the other panel; Unmount in the context menu; Open With for other programs | 4 | [x] |
| Thumbnails | 3 | [x] |
