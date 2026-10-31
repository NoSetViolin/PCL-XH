# Shared card blur smoke

The optional harness uses separate configuration, cache, profiles and instance locks.
Normal builds do not include its module initializer or diagnostic code. From the repository root:

```powershell
$cardTargets = Join-Path (Get-Location) 'tools/card-blur-smoke/Inject.targets'
$cardOutput = Join-Path (Get-Location) 'artifacts/card-blur/diagnostic/'
dotnet build 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' -c Release -p:Platform=x64 "-p:CustomBeforeMicrosoftCommonTargets=$cardTargets" "-p:OutDir=$cardOutput" -p:IntermediateOutputPath=obj/CardBlurSmoke/
$env:PCL_CARD_QA_ROOT = Join-Path (Get-Location) 'artifacts/card-blur/qa'
$env:PCL_CARD_QA_ANIME = '1'
Start-Process -FilePath (Join-Path $cardOutput 'Plain Craft Launcher 2.exe') -WindowStyle Hidden
```

Wait for `report.txt` in the QA root and close the diagnostic process before rebuilding.
The test saves native window PNGs, compares stripe edges with/without blur, checks shared
half-resolution caches, live source updates, idle invalidation, crop movement, radius cap,
kernel separation, software-HWND fallback, mode transitions, resize, unload and blur disabling.
Actual `MyCard` fixtures check rest/hover/leave in both themes, faint contrasting gray
outlines and native white pixels outside each edge to detect an unwanted dark halo.
With `PCL_CARD_QA_ANIME=1`, real random-anime backgrounds are captured first for daily-use
appearance checks. Deterministic fixtures still establish pixel-level edge correctness.
If the random-image service is unavailable, `PCL_CARD_QA_ANIME_CACHE` may point to a
previous image downloaded by that service; only the isolated QA cache is seeded.
The report records the normal background service's status so cached reuse is explicit.
Set `PCL_CARD_QA_SIDEBAR_ONLY=1` for the focused folder-list pass: it checks the actual
InstanceSelect sidebar in Chinese/English, both themes and window sizes, with uniform
input pixels sampled 4/8/12/20 DIP inside every edge. This detects inner rims that an
exterior-only shadow check cannot catch. Anime loading requires the normal image service.
It also checks homepage/settings/download navigation in Chinese/English, light/dark and
810×470/1350×825 layouts. The download captures check card rendering, not metadata loading.
These are native captures at the current monitor DPI, not an OS accessibility or real
video decoder test. An animated source and live color changes exercise retained rendering.

CPU time and WPF callback intervals for twelve cards over an animated source are a short
diagnostic comparison of flat/shared/full-lens paths, not GPU timings or displayed FPS.
No frame-rate improvement percentage should be inferred from this single-process fixture.
