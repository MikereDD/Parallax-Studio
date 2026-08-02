# Native runtime dependencies

Run:

```powershell
.\build.ps1 -Bootstrap -InstallBootstrapTools -Run
```

The bootstrap installs the pinned libmpv build and attempts the optional disc helpers `libaacs.dll`, `libbdplus.dll`, and `libdvdcss-2.dll`.

DVD support uses libdvdcss 1.5.0 built from VideoLAN source by the MSYS2 x64 CI package. The package archive is pinned by SHA-256 before extraction. No AACS keys are included or downloaded.
