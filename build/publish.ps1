# SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds a release folder:
#
#   VibeSalamander\
#     VibeSalamander.exe      the starter (runs lib\VibeSalamander.exe)
#     README.md, LICENSE.txt, CREDITS.md, AUTHORS.txt
#     lib\                    the program, its libraries, languages and plugins
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

Write-Host "Vibe Salamander ($Runtime) is in $out"
