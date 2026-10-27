# Troubleshooting

## "Windows protected your PC"

Windows SmartScreen shows this for programs that haven't been downloaded by many people yet, and for programs
without a code signing certificate. Click **More info**, then **Run anyway**. You only have to do it once.

If you prefer to check first: the release page lists the SHA-256 of each zip, and the whole program is built from
the source in this repository by a public [GitHub Actions workflow](https://github.com/BrainCluster/vibe-salamander/actions/workflows/release.yml).

## Something went wrong

Vibe Salamander writes errors to an **error log**, one file per day: **Help → Error Log** opens it. The logs are in
`%LocalAppData%\VibeSalamander\Logs` (`errors-YYYY-MM-DD.log`).

- If it closed because of an error, it says so the next time it starts.
- A failed copy or delete shows a notification with the reason; the details go to the log too.

## Reporting a bug

[Open a bug report](https://github.com/BrainCluster/vibe-salamander/issues/new/choose) and tell what you did, what
you expected and what happened. **Attach the error log** of that day: it says where the problem was. The log
contains file paths from your PC; remove any you'd rather not share.

The version is in Help → About.

## Settings

All settings are in one file: **`%AppData%\VibeSalamander\settings.json`**. Configuration → Backup exports and
imports it and opens its folder.

- If the file gets damaged (for example by a crash while saving), Vibe Salamander keeps what it can read, sets
  the rest back to defaults, keeps the damaged file as `settings.broken-<date>.json` and tells you.
- To start over with the defaults: close Vibe Salamander and delete or rename `settings.json`.

Other things in `%AppData%\VibeSalamander`: `Plugins` (plugins you installed) and `PluginData` (their settings).

### Portable or separate settings

The environment variable **`VIBE_SALAMANDER_HOME`** moves everything (settings, plugins, logs) into one folder of
your choice, for example on a USB drive, or to try something without touching your normal settings:

```bat
set VIBE_SALAMANDER_HOME=D:\VibeSalamanderData
start "" "D:\VibeSalamander\VibeSalamander.exe"
```

## A plugin doesn't show up

- The folder layout must be `Plugins\<Name>\<Name>.dll`, in `%AppData%\VibeSalamander\Plugins` (or in `lib\Plugins`
  next to the program), and Vibe Salamander must be restarted.
- Plugins → Plugins Manager lists plugins that failed to load, with the reason; it also lists turned-off plugins.

## Thumbnails don't appear

Vibe Salamander gets thumbnails from Windows. If Explorer shows only icons too, check File Explorer → Options →
View → *Always show icons, never thumbnails* is off.
