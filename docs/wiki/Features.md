# Features

## Panels

- Two panels with Salamander's keyboard model: Tab to switch, quick search by typing, Insert / Space to mark,
  Num + / − / * to select by mask.
- **Five views**: Brief, Detailed, Icons, Thumbnails and Tiles (Alt+1…5). In Detailed view the columns can be
  chosen and resized; columns that don't fit a narrow panel step aside so names stay readable.
- **Classic or Modern folder views** (Configuration → Views): one view per panel everywhere, or File Explorer's
  way: tiles for This PC, thumbnails for folders of pictures and videos, and a remembered view for each folder.
- A Windows 11 style address bar with a drive menu, clickable path segments and subfolder chevrons; drive bars;
  This PC with fullness bars.
- **Live drives**: plug in a USB drive or a memory card and it opens in the active panel; pull it out and the
  panel goes back to This PC.
- Hot paths (Ctrl+1…9), back and forward history, a filter (Ctrl+F12), hidden and system files on or off, maximize
  or swap the panels.
- Rectangle selection with the mouse, drag and drop between the panels and to or from Explorer, automatic refresh
  that keeps the cursor and the marks.

![Thumbnails of a photo folder, documents in the other panel, in the dark theme](images/hero-dark.png)

## File operations

- Copy, move, delete, rename in place (F2), change attributes and case, NTFS compression and encryption,
  properties and security.
- **The transfer panel**: copies, moves and deletes run in the background, several at once or queued, each with
  pause, resume and cancel. You keep working while they run.
- One question when a file exists (overwrite, skip, keep both, older only, each *for all*), and errors as
  notifications that close themselves rather than dialogs you have to click away.
- Careful with links: folder links and junctions are copied as links, never followed into loops, and a move
  never deletes what a link points to.
- F3 views and F4 edits in the built-in viewer and editor (below); Open with (Alt+F3), and per-mask viewers and
  editors of your own.
- Right-click: a short *Vibe* menu with the most useful commands, and the full Windows context menu one click away.
- New folders and files are created and named in place.

![The transfer panel](images/transfer-panel.png)

## View and edit

**The viewer (F3)** opens anything, whatever its size:

- **Text and hex** for files of any size: a multi-gigabyte log opens at once and is indexed while you read. Encodings
  (UTF-8/16, ANSI, OEM and more) are detected and can be switched; find text, whole words, regular expressions or hex
  bytes (Ctrl+F, F3 / Shift+F3); go to a line or an offset (Ctrl+G); wrap (W) and line numbers.
- **JSON, XML and YAML as a tree**, with a problem shown at its line when the file is broken.
- **Markdown, Jupyter notebooks and web pages rendered**: tables, task lists, code, pictures next to the file and
  notebook outputs. Nothing is loaded from the internet and no script runs unless you allow it.
- **Pictures**: JPEG, PNG, GIF, WebP, HEIC, AVIF, JPEG XL, SVG and camera RAW (with Windows' free extensions), zoom (also with the mouse wheel),
  rotate (R), and details: size, camera, lens, exposure, date taken, location.
- **PDFs**, page by page, and **audio and video** with their details.
- Ctrl+PgUp / Ctrl+PgDn (or ← / → for pictures) go to the previous or next file of the folder; Ctrl+1…7 switch between
  the views a file has; F4 edits it.

**The editor (F4)** is the editor of Visual Studio Code (Monaco):

- Syntax colors for more than 70 languages, folding, multiple cursors, the minimap and the command palette (F1).
- **JSON, XML and YAML are checked while you type**: problems are underlined and the first one is in the status line;
  Format (Shift+Alt+F) tidies JSON and XML.
- **A live preview** of Markdown and HTML beside the text.
- Find and replace (Ctrl+F, Ctrl+H) with regular expressions; go to line (Ctrl+G).
- The encoding and line endings are kept, or chosen when saving; characters the encoding can't hold are pointed out.
- **Safe saving**: the file is written next to the original and swapped in only when complete, with an optional
  backup (.bak). Changes made by another program are noticed, and closing never loses unsaved work silently.
- Files in archives and on FTP / SFTP servers are edited too: saving sends them back.

## Find, compare and measure

- **Find files** (Alt+F7) by name masks and text inside files, in subfolders too; the results are a list
  you can open, focus in the panel or work with.
- **Compare directories** (Ctrl+F10) by date, size or content (and inside subfolders): the differences are marked in
  both panels.
- Directory sizes (Ctrl+Shift+F10), occupied space (Alt+F10), drive information (Ctrl+F1), file lists (Ctrl+M).
- **Disk Map** (plugin): what takes the space, as blocks, a sunburst, columns or Salamander's classic treemap.

![Find results](images/find-results.png)

## Commands

- **User menu** (F9): your own commands, with variables such as `$(FullName)`, `$(Name)`, `$(SelectedFullNames)` and `$(OtherPath)`,
  and an optional user menu bar.
- A command line under the panels, a command shell (Num /), network drives and shares.
- **Mount disk images**: .iso, .img, .vhd and .vhdx open as drives in the other panel.

![The user menu](images/user-menu.png)

## The menus

| | |
|---|---|
| ![Files menu](images/menu-files.png) | ![Commands menu](images/menu-commands.png) |
| ![Plugins menu](images/menu-plugins.png) | ![Options menu](images/menu-options.png) |

## Looks

- Light, dark or the system's theme; Compact or Comfortable density; the panel font and its size.
- **Color schemes**: Vibe, Explorer, the terminal-inspired Yazi, Superfile, Tokyo Night, Gruvbox, Dracula, Nord
  and Phosphor, the retro Norton Commander and DOS Navigator, or your own. Files can be colored by mask.

![The Tokyo Night scheme](images/scheme-tokyo-night.png)

## Configuration

Options → Configuration has Open Salamander's pages, plus a search box: confirmations, panels, views, colors,
drives, the keyboard (every command can get any key), the toolbar, hot paths, the user menu, the transfer panel,
plugins, and backup (export and import of all settings).

| | |
|---|---|
| ![General](images/configuration-general.png) | ![Appearance](images/configuration-appearance.png) |
| ![Views](images/configuration-views.png) | ![Keyboard](images/configuration-keyboard.png) |

## Languages

English, Hrvatski, Čeština, Deutsch, Español, Français, Italiano, Nederlands, Polski, Português (Brasil),
Русский and Українська (Options → Configuration → General → Language).

## When something goes wrong

Errors are written to an error log (Help → Error Log). If Vibe Salamander closed because of an error, it tells you
the next time it starts; if the settings file was damaged, the broken copy is kept and the rest of your settings
are still used. See [Troubleshooting](Troubleshooting).
