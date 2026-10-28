# SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Finds Inno Setup's compiler (installs it with Chocolatey when it's missing) and puts its path into $env:ISCC
# (and into GITHUB_ENV on GitHub Actions).
$ErrorActionPreference = 'Stop'
function Find-Iscc {
    foreach ($root in "${env:ProgramFiles(x86)}", "$env:ProgramFiles", "$env:LocalAppData\Programs") {
        $found = Get-ChildItem -Path (Join-Path $root 'Inno Setup*\ISCC.exe') -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
}
$iscc = Find-Iscc
if (-not $iscc) {
    choco install innosetup --no-progress -y | Out-Host
    $iscc = Find-Iscc
}
if (-not $iscc) { throw "Inno Setup's ISCC.exe wasn't found." }
$env:ISCC = $iscc
if ($env:GITHUB_ENV) { "ISCC=$iscc" >> $env:GITHUB_ENV }
Write-Host "Inno Setup: $iscc"
