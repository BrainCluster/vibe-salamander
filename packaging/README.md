# Package managers

**Scoop**: this repository is also a Scoop bucket (`bucket/vibe-salamander.json`):

```powershell
scoop bucket add vibe-salamander https://github.com/BrainCluster/vibe-salamander
scoop install vibe-salamander
```

**winget**: each release gets its manifests in `winget/<version>/` (made by the release workflow with
`build/package-manifests.sh`, like the Scoop file). To publish a version in the winget catalog:

```powershell
winget install wingetcreate
wingetcreate submit --token <a GitHub token with public_repo> packaging\winget\<version>
```

That opens a pull request in microsoft/winget-pkgs; after its checks and a review it is installable with
`winget install BrainCluster.VibeSalamander` (later versions: `wingetcreate update BrainCluster.VibeSalamander ...`
or submit the new folder the same way).

The first winget version should be 0.5.0 or later: from 0.5.0 the starter finds the program also when it is
started through the link winget puts on the PATH.
