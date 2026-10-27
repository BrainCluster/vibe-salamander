# Installation

Vibe Salamander runs on **Windows 10 (version 2004) or later**, on x64 and ARM64 PCs. It is portable: nothing is
installed into Windows, and removing its folder removes the program (your settings stay in your profile).

## Download (portable zip)

1. Open the [latest release](https://github.com/BrainCluster/vibe-salamander/releases/latest).
2. Download `VibeSalamander-<version>-win-x64.zip` (most PCs) or `…-win-arm64.zip` (ARM PCs such as Snapdragon
   laptops). Not sure? Settings → System → About → *System type*.
3. Unzip it anywhere you like, for example `C:\Tools\VibeSalamander`.
4. Run **`VibeSalamander.exe`** in the main folder. (The program and its libraries are in `lib`; the small
   `VibeSalamander.exe` next to it starts them.)
5. Optional: right-click `VibeSalamander.exe` → *Pin to Start* or *Pin to taskbar*.

**"Windows protected your PC"?** That's SmartScreen being careful with programs it hasn't seen often yet.
Click **More info → Run anyway**. See [Troubleshooting](Troubleshooting#windows-protected-your-pc).

## Scoop

```powershell
scoop bucket add vibe-salamander https://github.com/BrainCluster/vibe-salamander
scoop install vibe-salamander
```

Scoop adds a Start menu entry and the `vibesalamander` command. Update with `scoop update vibe-salamander`.

## winget

Coming soon: once the package is in the winget catalog, `winget install BrainCluster.VibeSalamander`.

## The 7-Zip plugin (optional)

7-Zip's formats (7z with all of 7-Zip's settings, CAB, WIM, MSI, CHM, ARJ, LZH, RPM, DEB, DMG…) come as a separate
download, because the plugin includes 7-Zip itself:

1. Download `VibeSalamander-7-Zip-plugin-<version>-win-x64.zip` (or arm64) from the same release.
2. Copy its `7-Zip` folder into `%AppData%\VibeSalamander\Plugins\` (type that path into the address bar).
3. Restart Vibe Salamander. Plugins → Plugins Manager lists it.

An installed 7-Zip works too: the plugin finds it when 7z.exe isn't in its own folder.

## Updating

Download the new zip and unzip it over the old folder (or into a new one). Settings carry over: they are kept in
`%AppData%\VibeSalamander`, not in the program's folder.

## Uninstalling

Delete the program's folder. To remove your settings too, delete `%AppData%\VibeSalamander`
(and the error logs in `%LocalAppData%\VibeSalamander`).

## Building it yourself

You need Visual Studio 2026 with the *WinUI application development* and *.NET desktop development* workloads
and the .NET 10 SDK. Open `VibeSalamander.sln`, pick **Debug | x64** and the `VibeSalamander.App` project, press
F5. `build\publish.ps1` makes the same folder as a release.
