# Native runtime directory

Do not commit downloaded DLLs. Run:

```powershell
.\build.ps1 -Bootstrap -InstallBootstrapTools -Run
```

The bootstrap installs `libmpv-2.dll` and attempts the optional disc helpers `libaacs.dll`, `libbdplus.dll`, and `libdvdcss-2.dll`. No AACS keys are included or downloaded.
