# Vibe Salamander

A modern spiritual successor to [Altap / Open Salamander](https://github.com/OpenSalamander/salamander):
a fast, precise, keyboard-first **dual-pane** file manager for Windows, built with **C# and WinUI 3**
and a Fluent look in the spirit of the [Files](https://github.com/files-community/files) app.

> Vibe Salamander is to Altap Salamander what Files is to Windows File Explorer.

## Build and run

Requirements: Windows 10 1904x or later, **Visual Studio 2026** with the *WinUI application
development* (Windows App SDK) and *.NET desktop development* workloads, .NET 10 SDK.

1. Open `VibeSalamander.sln`.
2. Select **Debug | x64** (or ARM64) and the `VibeSalamander.App` startup project.
3. Press **F5**. The app runs unpackaged (no MSIX install), the Windows App SDK is bundled.

Settings are stored in `%AppData%\VibeSalamander\settings.json`.

## Keyboard (Salamander defaults)

| Keys | Action | Keys | Action |
|---|---|---|---|
| Tab | Switch panel | Enter | Open |
| Backspace / Ctrl+PgUp | Parent directory (drive root → This PC) | Shift+Backspace | Root |
| Alt+F1 / Alt+F2 | Change drive left / right | Alt+Left / Alt+Right | Back / Forward |
| F2 | Rename | F3 / F4 | View / Edit |
| F5 / F6 | Copy / Move | F7 | Create directory |
| F8, Del / Shift+Del | Delete (Recycle Bin / permanent) | Alt+Enter | Properties |
| Insert / Space | Mark item | Ctrl+A / Ctrl+D / Num * | Select all / none / invert |
| Ctrl+F3…F6 | Sort by name / ext / date / size | Ctrl+R, Ctrl+F9 | Refresh |
| Ctrl+U | Swap panels | Ctrl+F11 | Maximize panel |
| Ctrl+H | Hidden files | Ctrl+. | Path from other panel |
| Shift+F7 | Change directory | Shift+F3 | Open in Explorer |
| Num / | Command shell | typing | Quick search |

## Docs

* [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) – structure, design decisions, addon model
* [docs/ROADMAP.md](docs/ROADMAP.md) – phases and the Salamander feature-parity checklist

Open Salamander's source (`C:\dev\source\repos\OpenSalamander`) is the behavioral reference.

## License and credits

Copyright © 2026 Vibe Salamander Authors.

Vibe Salamander is free software, licensed under the **GNU General Public License v3.0 or later**
([LICENSE](LICENSE)). It is a spiritual successor to Open Salamander, and its application icon is
based on the Open Salamander icon (GPL-2.0-or-later); see [CREDITS.md](CREDITS.md) for the full
credits to the Servant / Altap / Open Salamander authors and the third-party components.
