# SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Builds the Git plugin's download, a folder for the Plugins folder:
#
#   Git\
#     VibeSalamander.Plugins.Git.dll (+ .deps.json)   the plugin
#     LibGit2Sharp.dll                                 reads repositories (MIT)
#     runtimes\<win-x64|win-arm64>\native\git2-*.dll   libgit2 for this processor (GPLv2 with the linking exception)
#     README.txt                                       how to install the plugin
#
# Usage:  build\publish-git-plugin.ps1 [-Runtime win-x64|win-arm64] [-Output publish\Git]

param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$Output = 'publish\Git'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$build = Join-Path ([IO.Path]::GetTempPath()) 'vibe-git-plugin'
if (Test-Path $build) { Remove-Item $build -Recurse -Force }
dotnet publish (Join-Path $repo 'src\VibeSalamander.Plugins.Git\VibeSalamander.Plugins.Git.csproj') -c Release -o $build
if ($LASTEXITCODE) { exit $LASTEXITCODE }
New-Item $out -ItemType Directory -Force | Out-Null
foreach ($file in Get-ChildItem $build -File) {
    # the app shares Core and the plugin contracts
    if ($file.Name -like 'VibeSalamander.Core*' -or $file.Name -like 'VibeSalamander.Plugins.Abstractions*') { continue }
    if ($file.Extension -eq '.pdb') { continue }
    if ($file.Extension -eq '.json' -and $file.Name -ne 'VibeSalamander.Plugins.Git.deps.json') { continue }
    Copy-Item $file.FullName $out
}
# libgit2 for this processor only (the deps.json still names the others: they're simply not there)
$native = Join-Path $build "runtimes\$Runtime\native"
if (-not (Test-Path $native)) { throw "libgit2 for $Runtime is not in the build ($native)." }
$target = New-Item (Join-Path $out "runtimes\$Runtime\native") -ItemType Directory -Force
Copy-Item (Join-Path $native '*') $target
if (-not (Test-Path (Join-Path $out 'VibeSalamander.Plugins.Git.dll'))) { throw 'The plugin was not built.' }

@"
Vibe Salamander - Git plugin ($Runtime)

Install: put this "Git" folder into

    %AppData%\VibeSalamander\Plugins\

(Plugins -> Plugins Manager -> Open plugins folder opens it), or into the "lib\Plugins" folder of
Vibe Salamander, and start Vibe Salamander again.

In the panels: the branch with commits to push and to pull, each file colored by its Git state, and a
Git column. Plugins -> Git: changes (stage, unstage, discard, commit, compare), history (a commit graph,
blame, browse an old commit's files), fetch, pull, push and clone.

Repositories are read inside Vibe Salamander by libgit2 (through LibGit2Sharp), which never runs a hook
or any other program a repository names. Committing, fetching, pulling, pushing and cloning run in your
own Git (git.exe, https://git-scm.com), by its full path.

libgit2 is GPLv2 with the linking exception, LibGit2Sharp is MIT; the plugin is GPL-3.0-or-later, like
Vibe Salamander.
"@ | Set-Content (Join-Path $out 'README.txt') -Encoding UTF8

Write-Host "The Git plugin ($Runtime) is in $out"
