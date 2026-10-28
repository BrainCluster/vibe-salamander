# Installation

Vibe Salamander runs on **Windows 10 (version 2004) or later**, on x64 and ARM64 PCs. It comes two ways: an
**installer**, and a **portable zip** that installs nothing into Windows. Not sure whether your PC is x64 or ARM64?
Settings → System → About → *System type* (ARM64 PCs are for example Snapdragon laptops).

## Installer

1. Open the [latest release](https://github.com/BrainCluster/vibe-salamander/releases/latest).
2. Download `VibeSalamander-<version>-win-x64-setup.exe` (most PCs) or `…-win-arm64-setup.exe` (ARM PCs).
3. Run it. **For me only** installs into `%LocalAppData%\Programs\Vibe Salamander` without administrator rights;
   **for all users** installs into Program Files (Windows asks for permission).
4. It adds Vibe Salamander to the Start menu (and, if you tick it, to the desktop) and an uninstaller.

Settings are kept in `%AppData%\VibeSalamander`, so they stay when you update or reinstall.

## Portable zip

1. Download `VibeSalamander-<version>-win-x64.zip` (or `…-win-arm64.zip`) from the same release.
2. Unzip it anywhere you like: `C:\Tools\VibeSalamander`, a USB stick, a synced folder.
3. Run **`VibeSalamander.exe`** in the main folder. (The program and its libraries are in `lib`; the small
   `VibeSalamander.exe` next to it starts them.)

The zip is **portable**: the `portable.flag` file next to `VibeSalamander.exe` keeps the settings, plugins, logs and
caches in a `Data` folder beside the program, and nothing in your profile. Delete `portable.flag` to keep the
settings in `%AppData%\VibeSalamander` instead. Coming from a version that kept them in `%AppData%`? The first start
of a portable copy copies them into `Data`.

**"Windows protected your PC"?** The program isn't code-signed yet, so SmartScreen is careful with it.
Click **More info → Run anyway**. See [Troubleshooting](Troubleshooting#windows-protected-your-pc).

## Scoop

```powershell
scoop bucket add vibe-salamander https://github.com/BrainCluster/vibe-salamander
scoop install vibe-salamander
```

Scoop installs the portable zip and keeps its `Data` folder between updates; it adds a Start menu entry and the
`vibesalamander` command. Update with `scoop update vibe-salamander`.

## winget

`winget install BrainCluster.VibeSalamander`, once the package is in the winget catalog (versions after 0.7.0 install
with the installer).

## Updating

**Help → Check for Updates** asks GitHub for the newest version; by default Vibe Salamander also asks once a day
(Configuration → General → *Check for updates once a day*; nothing but that question is sent). When there is one,
it shows what's new, and **Update now**:

- an **installed** copy downloads the new installer, which installs over it silently and starts the program again
  (for an installation for all users, Windows asks for permission);
- a **portable** copy downloads the new zip; once Vibe Salamander has closed, the new files replace the old ones
  (the `Data` folder and anything else of yours stays) and it starts again;
- a **Scoop** copy says to run `scoop update vibe-salamander`.

Each download is checked against the size and SHA-256 that GitHub gives for it. *Skip this version* stops the daily
check from offering that version again. Running transfers and open editor windows are finished first.

By hand: run the new installer, or unzip the new zip over the old folder.

## The 7-Zip plugin (optional)

7-Zip's formats (7z with all of 7-Zip's settings, CAB, WIM, MSI, CHM, ARJ, LZH, RPM, DEB, DMG…) come as a separate
download, because the plugin includes 7-Zip itself:

1. Download `VibeSalamander-7-Zip-plugin-<version>-win-x64.zip` (or arm64) from the same release.
2. Copy its `7-Zip` folder into the `Plugins` folder of your settings: `%AppData%\VibeSalamander\Plugins\` for an
   installed copy, `Data\Plugins\` beside the program for a portable one.
3. Restart Vibe Salamander. Plugins → Plugins Manager lists it.

An installed 7-Zip works too: the plugin finds it when 7z.exe isn't in its own folder.

## Uninstalling

Installed: Windows Settings → Apps → Vibe Salamander → Uninstall. Portable: delete its folder (the settings are in it).
The settings of an installed copy stay; to remove them too, delete `%AppData%\VibeSalamander` (and the error logs in
`%LocalAppData%\VibeSalamander`).

## Building it yourself

You need Visual Studio 2026 with the *WinUI application development* and *.NET desktop development* workloads
and the .NET 10 SDK. Open `VibeSalamander.sln`, pick **Debug | x64** and the `VibeSalamander.App` project, press
F5. `build\publish.ps1` makes the same folder as a release; `build\installer\VibeSalamander.iss` (Inno Setup 6.3 or
later) makes the installer from it.
