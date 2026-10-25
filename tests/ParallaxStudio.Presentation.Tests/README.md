# Presentation regression checks

Run on Windows x64 with the .NET 10 SDK:

```powershell
.\scripts\Test-Presentation.ps1
```

Add `-Configuration Release` for a Release build or `-PreviewDirectory .\artifacts\presentation-preview` to render the shell across themes and densities without opening a playback window.

Or run the executable project directly:

```powershell
dotnet run --project .\tests\ParallaxStudio.Presentation.Tests\ParallaxStudio.Presentation.Tests.csproj
```

This dependency-free check executable references the real WPF app. It verifies local artwork priority, corrupt/missing/oversize fallback, bounded decoding, file-handle release, frozen images, cancellation, badge accuracy, optical source/title summaries, missing detail fields, subtitle state, and runtimes over 24 hours. Each artwork check creates and removes its own fixtures inside the ignored build output's `work` directory. A failure returns a nonzero exit code.

No native playback library, MakeMKV installation, media collection, or optical drive is needed. Playback, native HWND behavior, fullscreen controls, optical reconciliation, and visual layout still require the manual checks in [TESTING.md](../../TESTING.md).
