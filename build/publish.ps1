# SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds a release folder:
#
#   VibeSalamander\
#     VibeSalamander.exe      the starter (runs lib\VibeSalamander.exe)
#     README.md, LICENSE.txt, CREDITS.md, AUTHORS.txt
#     lib\                    the program, its libraries, languages and plugins
#     lib\7-Zip\             7-Zip (7z.exe, 7z.dll, 7z.sfx) for the 7-Zip plugin, when it could be fetched
#
# Usage:  build\publish.ps1 [-Runtime win-x64|win-arm64] [-Output publish\VibeSalamander]

param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Output = 'publish\VibeSalamander'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
$lib = Join-Path $out 'lib'
$starter = Join-Path $out '.starter'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# the program and everything it needs, into lib
dotnet publish (Join-Path $repo 'src\VibeSalamander.App\VibeSalamander.App.csproj') `
    -c Release -r $Runtime -p:Platform=$platform --self-contained true -p:PublishReadyToRun=true -o $lib
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# the starter, into the main folder
dotnet publish (Join-Path $repo 'src\VibeSalamander.Launcher\VibeSalamander.Launcher.csproj') -c Release -o $starter
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Copy-Item (Join-Path $starter 'VibeSalamander.exe') $out
Remove-Item $starter -Recurse -Force

# the documents next to the starter
foreach ($doc in 'LICENSE.txt', 'CREDITS.md', 'AUTHORS.txt') {
    $path = Join-Path $lib $doc
    if (Test-Path $path) { Move-Item $path $out -Force }
}
Copy-Item (Join-Path $repo 'README.md') $out
Remove-Item (Join-Path $lib '*.pdb') -ErrorAction SilentlyContinue

# 7-Zip for the 7-Zip plugin (LGPL; its installer is itself a 7z archive): without it the plugin uses an installed 7-Zip
$sevenZipVersion = '2603'
$arch = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
try {
    $sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $sevenZip) { throw '7-Zip is needed to unpack the 7-Zip installer' }
    $download = Join-Path ([IO.Path]::GetTempPath()) "7z$sevenZipVersion-$arch.exe"
    Invoke-WebRequest "https://github.com/ip7z/7zip/releases/download/$($sevenZipVersion.Substring(0,2)).$($sevenZipVersion.Substring(2))/7z$sevenZipVersion-$arch.exe" -OutFile $download
    $unpacked = Join-Path ([IO.Path]::GetTempPath()) "7z$sevenZipVersion-$arch"
    if (Test-Path $unpacked) { Remove-Item $unpacked -Recurse -Force }
    & $sevenZip x $download "-o$unpacked" -y | Out-Null
    if ($LASTEXITCODE) { throw "7-Zip's installer could not be unpacked ($LASTEXITCODE)" }
    $target = Join-Path $lib '7-Zip'
    New-Item $target -ItemType Directory -Force | Out-Null
    foreach ($file in '7z.exe', '7z.dll', '7z.sfx', '7zCon.sfx', 'License.txt', 'readme.txt') {
        $path = Join-Path $unpacked $file
        if (Test-Path $path) { Copy-Item $path $target }
    }
    if (-not (Test-Path (Join-Path $target '7z.exe'))) { throw '7z.exe is not in the installer' }
    Write-Host "7-Zip $sevenZipVersion ($arch) is in $target"
}
catch {
    Write-Warning "7-Zip is not included: $_"
}

Write-Host "Vibe Salamander ($Runtime) is in $out"
