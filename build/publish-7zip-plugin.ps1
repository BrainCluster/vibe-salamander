# SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds the 7-Zip plugin's download, a folder for the Plugins folder:
#
#   7-Zip\
#     VibeSalamander.Plugins.SevenZip.dll   the plugin
#     7z.exe, 7z.dll, 7z.sfx, 7zCon.sfx     7-Zip (unchanged, from its installer)
#     License.txt, readme.txt               7-Zip's license (GNU LGPL + unRAR restriction) and notes
#     README.txt                            how to install the plugin
#
# Usage:  build\publish-7zip-plugin.ps1 [-Runtime win-x64|win-arm64] [-Output publish\7-Zip]
# Needs an installed 7-Zip to unpack 7-Zip's installer (GitHub's Windows runners have one).

param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Output = 'publish\7-Zip',
    [string]$SevenZipVersion = '26.03'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# the plugin (any CPU; the app shares Core and the plugin contracts with it, so those are left out)
$build = Join-Path ([IO.Path]::GetTempPath()) 'vibe-7zip-plugin'
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
dotnet publish (Join-Path $repo 'src\VibeSalamander.Plugins.SevenZip\VibeSalamander.Plugins.SevenZip.csproj') -c Release -o $build
if ($LASTEXITCODE) { exit $LASTEXITCODE }
New-Item $out -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $build 'VibeSalamander.Plugins.SevenZip.dll') $out

# 7-Zip for this processor, from its installer (which is itself a 7z archive)
$arch = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$short = $SevenZipVersion.Replace('.', '')
$sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sevenZip) { throw 'An installed 7-Zip is needed to unpack the 7-Zip installer.' }
$installer = Join-Path ([IO.Path]::GetTempPath()) "7z$short-$arch.exe"
Invoke-WebRequest "https://github.com/ip7z/7zip/releases/download/$SevenZipVersion/7z$short-$arch.exe" -OutFile $installer
$unpacked = Join-Path ([IO.Path]::GetTempPath()) "7z$short-$arch"
if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
& $sevenZip x $installer "-o$unpacked" -y | Out-Null
if ($LASTEXITCODE) { throw "The 7-Zip installer could not be unpacked ($LASTEXITCODE)." }
foreach ($file in '7z.exe', '7z.dll', '7z.sfx', '7zCon.sfx', 'License.txt', 'readme.txt') {
    $path = Join-Path $unpacked $file
    if (Test-Path $path) { Copy-Item $path $out }
}
if (-not (Test-Path (Join-Path $out '7z.exe'))) { throw '7z.exe is not in the 7-Zip installer.' }

@"
Vibe Salamander - 7-Zip plugin (with 7-Zip $SevenZipVersion, $arch)

Install: put this "7-Zip" folder into

    %AppData%\VibeSalamander\Plugins\

(Plugins -> Plugins Manager -> Open plugins folder opens it), or into the "lib\Plugins" folder of
Vibe Salamander, and start Vibe Salamander again. Plugins -> 7-Zip then appears; 7z archives and
the formats only 7-Zip reads (CAB, WIM, MSI, CHM, ARJ, LZH, RPM, DEB, DMG, ...) go through 7-Zip.

7-Zip is by Igor Pavlov (www.7-zip.org), licensed under the GNU LGPL with the unRAR restriction:
see License.txt. The files here are 7-Zip's own, unchanged. The plugin is GPL-3.0-or-later, like
Vibe Salamander.
"@ | Set-Content (Join-Path $out 'README.txt') -Encoding UTF8

Write-Host "The 7-Zip plugin ($Runtime) is in $out"
