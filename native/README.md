# Native runtime files

`libmpv-2.dll` is required for playback and intentionally excluded from Git.

Install the pinned x64-v3 build automatically:

```powershell
.\scripts\Get-NativeDependencies.ps1
```

If 7-Zip is not installed:

```powershell
.\scripts\Get-NativeDependencies.ps1 -InstallTools
```

Or bootstrap while building:

```powershell
.\build.ps1 -Bootstrap -Run
```

`libdvdcss-2.dll` and `libaacs.dll` remain optional, user-supplied disc libraries until the disc-distribution policy is finalized.
