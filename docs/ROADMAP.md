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
4. **Addons** – addon loader, archives (ZIP/7z/TAR/RAR), FTP/SFTP, picture viewer, checksum,
   renamer, file compare, disk map … (ports of Salamander plugins).

## Left / Right panel menu
| Command | Keys | Phase | Done |
|---|---|---|---|
| Change drive | Alt+F1 / Alt+F2 | 1 | [x] |
| Back / Forward | Alt+Left / Alt+Right | 1 | [x] |
| Parent directory | Backspace | 1 | [x] |
| Root directory | Shift+Backspace | 1 | [x] |
| Path from other panel | Ctrl+. | 1 | [x] |
| Hot paths | Ctrl+Shift+0-9 / Ctrl+0-9 | 3 | [ ] |
| Sort by Name/Ext/Size/Date/Attr | Ctrl+F3…F6 | 1 | [x] |
| Views (Brief, Detailed, Icons, Thumbnails, Tiles) | Alt+1…9 | 3 | [ ] |
| Show directory/header/information line | | 1 | [~] |
| Choose columns | | 3 | [ ] |
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
| Email | Ctrl+E | 3 | [ ] |
| Properties | Alt+Enter | 1 | [x] |
| Security | Ctrl+P | 3 | [ ] |
| Change attributes | Ctrl+F2 | 2 | [x] |
| NTFS compress/uncompress/encrypt/decrypt | | 3 | [ ] |
| Change case | Ctrl+F7 | 2 | [x] |
| Convert | Ctrl+K | 4 | [ ] |
| Pack / Unpack | Alt+F5 / Alt+F9 | 4 | [ ] |
| New (shell "New" menu) | | 3 | [ ] |
| Exit | Alt+F4 | 1 | [x] |

## Edit menu
| Command | Keys | Phase | Done |
|---|---|---|---|
| Cut / Copy / Paste (clipboard) | Ctrl+X / Ctrl+C / Ctrl+V | 2 | [x] |
| Paste shortcut | Ctrl+S | 3 | [ ] |
| Copy full name / name / path / UNC path as text | Alt+Ins, Alt+Shift+Ins, Ctrl+Alt+Ins, Ctrl+Shift+Ins | 1 | [~] |
| Mark / unmark item | Insert, Space | 1 | [x] |
| Select… / Unselect… (mask) | Num + / Num − | 2 | [x] |
| Invert selection | Num * | 1 | [x] |
| Select all / Unselect all | Ctrl+A / Ctrl+D | 1 | [x] |
| Restore selection | Ctrl+W | 2 | [x] |
| Save / load selection | Ctrl+Shift+F5 / F6 | 3 | [ ] |
| Select/unselect same extension / name | Shift+Num± / Ctrl+Shift+Num± | 2 | [x] |
| Hide selected / unselected names, show hidden names | | 3 | [ ] |
| Go to previous / next selected | Alt+Up / Alt+Down | 2 | [x] |

## Commands menu
| Command | Keys | Phase | Done |
|---|---|---|---|
| Create directory | F7 | 1 | [x] |
| Change directory | Shift+F7 | 1 | [x] |
| Compare directories | Ctrl+F10 | 3 | [ ] |
| Calculate occupied space / directory sizes | Alt+F10 / Ctrl+Shift+F10 | 3 | [ ] |
| Drive information | Ctrl+F1 | 3 | [ ] |
| Find files and directories | Alt+F7 | 2 | [x] |
| Make file list | Ctrl+M | 3 | [ ] |
| Go to shortcut target | Ctrl+T | 2 | [x] |
| Opened files / working directories lists | Alt+F11 / Alt+F12 | 3 | [ ] |
| Connect / disconnect network drive, shares | F11 / F12 / Ctrl+Shift+F9 | 3 | [ ] |
| Command shell | Num / | 1 | [x] |
| User menu | F9 | 3 | [ ] |
| Open folder (Desktop, Computer, Control Panel, Recycle Bin …) | | 3 | [ ] |
| Open active folder in Explorer | Shift+F3 | 1 | [x] |

## Plugins menu → Addons
| Command | Phase | Done |
|---|---|---|
| Addons manager | 4 | [ ] |
| Last command (Ctrl+I) | 4 | [ ] |

## Options menu
| Command | Phase | Done |
|---|---|---|
| Configuration dialog | 3 (basic settings in 1) | [ ] |
| Show/hide bars (toolbars, drive bars, command line, F-key bar, transfer panel) | 1 | [x] |
| F-key bar follows held Ctrl / Alt / Shift (F1–F12) | 2 | [x] |
| Ask for copy/move target (on/off) | 2 | [x] |
| Address bar in Windows 11 style: This PC › drive › folders, clickable segments, subfolder chevrons, click empty area to type (new) | 2 | [x] |
| Transfer panel: background queue, parallel transfers, pause/resume/cancel (new, WinSCP-style) | 2 | [x] |
| Customize toolbars | 3 | [ ] |
| Always on top | 1 | [x] |
| Export / import configuration | 3 | [ ] |
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
| Context menu (shell) | 2 | [x] |
| Thumbnails | 3 | [ ] |
