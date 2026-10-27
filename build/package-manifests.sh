#!/usr/bin/env bash
# The package manager files of a release, from its zips:
#   bucket/vibe-salamander.json                                   Scoop (this repository is a Scoop bucket)
#   packaging/winget/<version>/BrainCluster.VibeSalamander*.yaml  winget (submit with: wingetcreate submit <folder>)
# Usage: build/package-manifests.sh <version> <folder with the release zips> [release date YYYY-MM-DD]
# (SHA_X64 / SHA_ARM64 in the environment instead of the zips: an earlier release, from its published digests)
# The release workflow runs it after each release and commits the result.
set -euo pipefail
version="$1"
dist="$2"
date="${3:-$(date -u +%F)}"
repo="https://github.com/BrainCluster/vibe-salamander"
x64="VibeSalamander-${version}-win-x64.zip"
arm64="VibeSalamander-${version}-win-arm64.zip"
sha_x64=${SHA_X64:-$(sha256sum "$dist/$x64" | cut -d' ' -f1)}
sha_arm64=${SHA_ARM64:-$(sha256sum "$dist/$arm64" | cut -d' ' -f1)}
root=$(cd "$(dirname "$0")/.." && pwd)

# ---- Scoop -------------------------------------------------------------------------------------------
cat > "$root/bucket/vibe-salamander.json" <<JSON
{
    "version": "${version}",
    "description": "Fast, keyboard-first dual-pane file manager for Windows: a modern spiritual successor to Altap / Open Salamander.",
    "homepage": "${repo}",
    "license": "GPL-3.0-or-later",
    "notes": "Settings are in %AppData%\\\\VibeSalamander. The 7-Zip plugin is a separate download on the release page.",
    "architecture": {
        "64bit": {
            "url": "${repo}/releases/download/v${version}/${x64}",
            "hash": "${sha_x64}"
        },
        "arm64": {
            "url": "${repo}/releases/download/v${version}/${arm64}",
            "hash": "${sha_arm64}"
        }
    },
    "extract_dir": "VibeSalamander",
    "bin": "VibeSalamander.exe",
    "shortcuts": [
        [
            "VibeSalamander.exe",
            "Vibe Salamander"
        ]
    ],
    "checkver": {
        "github": "${repo}"
    },
    "autoupdate": {
        "architecture": {
            "64bit": {
                "url": "${repo}/releases/download/v\$version/VibeSalamander-\$version-win-x64.zip"
            },
            "arm64": {
                "url": "${repo}/releases/download/v\$version/VibeSalamander-\$version-win-arm64.zip"
            }
        }
    }
}
JSON

# ---- winget ------------------------------------------------------------------------------------------
out="$root/packaging/winget/${version}"
mkdir -p "$out"
id="BrainCluster.VibeSalamander"
cat > "$out/${id}.yaml" <<YAML
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.version.1.9.0.schema.json
PackageIdentifier: ${id}
PackageVersion: ${version}
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.9.0
YAML
cat > "$out/${id}.installer.yaml" <<YAML
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.installer.1.9.0.schema.json
PackageIdentifier: ${id}
PackageVersion: ${version}
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: VibeSalamander\\VibeSalamander.exe
  PortableCommandAlias: vibesalamander
ReleaseDate: ${date}
Installers:
- Architecture: x64
  InstallerUrl: ${repo}/releases/download/v${version}/${x64}
  InstallerSha256: ${sha_x64^^}
- Architecture: arm64
  InstallerUrl: ${repo}/releases/download/v${version}/${arm64}
  InstallerSha256: ${sha_arm64^^}
ManifestType: installer
ManifestVersion: 1.9.0
YAML
cat > "$out/${id}.locale.en-US.yaml" <<YAML
# yaml-language-server: \$schema=https://aka.ms/winget-manifest.defaultLocale.1.9.0.schema.json
PackageIdentifier: ${id}
PackageVersion: ${version}
PackageLocale: en-US
Publisher: BrainCluster
PublisherUrl: https://github.com/BrainCluster
PublisherSupportUrl: ${repo}/issues
Author: Claude (Anthropic) with BrainCluster
PackageName: Vibe Salamander
PackageUrl: ${repo}
License: GPL-3.0-or-later
LicenseUrl: ${repo}/blob/main/LICENSE
Copyright: Copyright (c) 2026 Vibe Salamander Authors
ShortDescription: Fast, keyboard-first dual-pane file manager: a modern spiritual successor to Altap / Open Salamander.
Description: |-
  Vibe Salamander is a dual-pane file manager for Windows with Salamander's keyboard model, a Windows 11 look,
  five views, a background transfer panel, archives (ZIP, 7z, RAR, TAR...), FTP / SFTP, checksums, a disk map,
  color schemes and 12 languages. Free software under the GPL.
Moniker: vibesalamander
Tags:
- file-manager
- dual-pane
- orthodox-file-manager
- salamander
- explorer-alternative
- ftp
- sftp
- zip
ReleaseNotesUrl: ${repo}/releases/tag/v${version}
Documentations:
- DocumentLabel: Wiki
  DocumentUrl: ${repo}/wiki
ManifestType: defaultLocale
ManifestVersion: 1.9.0
YAML
echo "Scoop: bucket/vibe-salamander.json; winget: packaging/winget/${version}"
