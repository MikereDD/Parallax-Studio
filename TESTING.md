# Parallax Studio v1.0-dev.4.2.3 Test

From a fresh extraction, leave `native\` without `libmpv-2.dll`, then run:

```powershell
.\build.ps1 -Bootstrap -InstallBootstrapTools -Run
```

Expected stages:

1. `Parallax Studio build script started.`
2. `Bootstrapping native dependencies...`
3. `Using 7-Zip: C:\Program Files\7-Zip\7z.exe`
4. Download or cached-archive message
5. Extraction and DLL installation
6. Native dependency verification
7. Normal .NET build and app launch

Verify afterward:

```powershell
Test-Path .\native\libmpv-2.dll
Test-Path .\native-dependencies.lock.json
Get-Content .\native\bootstrap.log -Tail 100
```

The first two commands should return `True`.
