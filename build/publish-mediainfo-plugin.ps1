# SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds the Media Info plugin's download, a folder for the Plugins folder:
#
#   Media Info\
#     VibeSalamander.Plugins.MediaInfo.dll (+ .deps.json)   the plugin
#     the MediaInfoDLL wrapper's assembly                    calls MediaInfo's library (BSD-2-Clause)
#     lib\MediaInfo\MediaInfo.dll, License.html              MediaInfo's library for this processor (BSD-2-Clause)
#     README.txt                                             how to install the plugin
#
# Usage:  build\publish-mediainfo-plugin.ps1 [-Runtime win-x64|win-arm64] [-Output "publish\Media Info"]

param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Output = 'publish\Media Info'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# the plugin and its managed dependencies (any CPU; the app shares Core and the plugin contracts, so those stay out)
$build = Join-Path ([IO.Path]::GetTempPath()) 'vibe-mediainfo-plugin'
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
dotnet publish (Join-Path $repo 'src\VibeSalamander.Plugins.MediaInfo\VibeSalamander.Plugins.MediaInfo.csproj') -c Release -o $build
if ($LASTEXITCODE) { exit $LASTEXITCODE }
New-Item $out -ItemType Directory -Force | Out-Null
foreach ($file in Get-ChildItem $build -File) {
    if ($file.Name -like 'VibeSalamander.Core*' -or $file.Name -like 'VibeSalamander.Plugins.Abstractions*') { continue }
    if ($file.Extension -eq '.json' -and $file.Name -ne 'VibeSalamander.Plugins.MediaInfo.deps.json') { continue }
    if ($file.Extension -eq '.pdb') { continue }
    if ($file.Extension -eq '.dll') {
        # managed assemblies only: MediaInfo's native library goes below, the one for this processor
        try { [Reflection.AssemblyName]::GetAssemblyName($file.FullName) | Out-Null } catch { continue }
    }
    Copy-Item $file.FullName $out
}
if (-not (Test-Path (Join-Path $out 'VibeSalamander.Plugins.MediaInfo.dll'))) { throw 'The plugin was not built.' }

# MediaInfo's library for this processor, from the MediaInfoDLL package the build restored
$arch = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
[xml]$project = Get-Content (Join-Path $repo 'src\VibeSalamander.Plugins.MediaInfo\VibeSalamander.Plugins.MediaInfo.csproj')
$version = ($project.Project.ItemGroup.PackageReference | Where-Object { $_.Include -eq 'MediaInfoDLL' }).Version
$package = Join-Path $packages "mediainfodll\$version"
$native = Join-Path $package "lib\netstandard2.0\$arch\MediaInfo.dll"
if (-not (Test-Path $native)) { throw "MediaInfo's library for $arch is not in $package." }
$lib = New-Item (Join-Path $out 'lib\MediaInfo') -ItemType Directory -Force
Copy-Item $native $lib
foreach ($license in 'License.html', 'LICENSE', 'License.txt') {
    $path = Join-Path $package $license
    if (Test-Path $path) { Copy-Item $path $lib }
}

@"
Vibe Salamander - Media Info plugin ($arch)

Install: put this "Media Info" folder into

    %AppData%\VibeSalamander\Plugins\

(Plugins -> Plugins Manager -> Open plugins folder opens it), or into the "lib\Plugins" folder of
Vibe Salamander, and start Vibe Salamander again. Then:

  - Plugins -> Media Info -> Media Info..., or "Media Info..." in the right-click menu of a video,
    audio or picture file: the details the MediaInfo app shows (container, tracks, codecs, bit rates,
    resolution, frame rate, HDR, channels, languages, chapters, tags), to copy or save as text, HTML,
    XML or JSON.
  - F3 on a video or audio file shows the report in the viewer.

The details are read by MediaInfo's library (MediaInfoLib, by MediaArea.net, BSD-2-Clause: see
lib\MediaInfo) through the MediaInfoDLL wrapper (BSD-2-Clause). The plugin is GPL-3.0-or-later,
like Vibe Salamander.
"@ | Set-Content (Join-Path $out 'README.txt') -Encoding UTF8

Write-Host "The Media Info plugin ($Runtime) is in $out"
