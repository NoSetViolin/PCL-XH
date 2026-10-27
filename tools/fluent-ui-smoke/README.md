# Fluent UI smoke checks

## Sustained frame pacing investigation

Run `tools/fluent-ui-smoke/Run-FramePacing.ps1 -OutputRoot artifacts/frame-pacing/baseline -SystemTrace`
from the repository root. It builds an isolated Release executable, warms each scenario
for 2 seconds, then captures three 10-second trials of direct scrolling, routed MouseWheel
input through production easing, dock dragging and navigation. The matrix runs with normal
materials, a frozen dock backdrop snapshot, and disabled dock glass. Overrides and resource
bindings are restored in `finally`. Snapshots can omit other ps_3_0 effects, so they isolate
scene dependency cost rather than establish visual equivalence.

Use a fresh output directory per run. QA data/config paths are isolated under `qa/`
via `PCL_FRAME_QA_ROOT`. Install managed tracing with
`dotnet tool install dotnet-trace --tool-path artifacts/frame-pacing/tools`.
The script captures managed stack samples, GC events and `PCL-FramePacing` markers.
WPR needs system profiling permission; failure is recorded without cancelling existing
recordings. Open `managed.nettrace` in PerfView or Visual Studio; use `system.etl` in WPA
when system capture succeeds for native render-thread scheduling and GPU/DWM events.

Trials write raw QPC timestamps/durations, callback intervals, GC counts, LayoutUpdated
counts, Render-priority dispatcher durations, and wheel-to-offset response. Dispatcher
duration includes other work and is **not** exact layout cost. Routed wheel input excludes
HID/OS delivery and its timer depends on dispatcher availability. Pending inputs pair with
their first subsequent offset change, not proof of causality or input-to-photon latency.
GDI refresh is nominal; callback timing is **not** displayed FPS. Instrumentation and stack
sampling add overhead; compare equally instrumented runs on the same display/configuration.

Summarize with `python tools/fluent-ui-smoke/summarize_frame_pacing.py artifacts/frame-pacing/baseline`.
The runner uses separate `obj/FramePacing-Release/` intermediates. Normal builds omit the
harness and backdrop override. Dormant probe scopes emit no events.
For a focused recheck, add `frame-pacing-normal-only.flag` beside the diagnostic executable
to repeat all four normal scenarios (three 10-second trials each), or
`frame-pacing-verify-only.flag` to check timestamp deduplication including subscription restarts.
The harness pins a frozen DrawingBrush wallpaper and dark theme and removes background video
for consistent process-to-process comparisons, restoring them on exit. For a paired baseline,
add `frame-pacing-baseline.flag` to bypass the new timestamp filters in the same diagnostic
binary. This bypass is compiled out of normal builds. Do not compare random startup wallpapers.

For the WPF Liquid Glass material, hardware screenshot checks and shader build
instructions, see [LIQUID-GLASS.md](LIQUID-GLASS.md).

This optional harness compiles into an isolated launcher build. Normal builds do not include it. The test process exits after writing its report.
It uses separate temporary configuration, profiles, cache and instance-lock directories. It does
not sign in, install Minecraft, or launch a game. With no flag it runs the full UI smoke; adding
one flag selects that focused diagnostic instead.

From the repository root in PowerShell:

```powershell
$smokeTargets = Join-Path (Get-Location) 'tools/fluent-ui-smoke/Inject.targets'
$smokeOutput = Join-Path $env:TEMP 'pcl-ui-efficiency-integration/bin/'
dotnet build 'Plain Craft Launcher 2/Plain Craft Launcher 2.csproj' --no-restore "-p:CustomBeforeMicrosoftCommonTargets=$smokeTargets" "-p:OutDir=$smokeOutput"
Start-Process -FilePath (Join-Path $smokeOutput 'Plain Craft Launcher 2.exe') -WindowStyle Hidden
```

Wait for `report.txt` in `%TEMP%/pcl-ui-efficiency-integration`. Screenshots are written beside it in
`screenshots/`. Close this test process before rebuilding the same output directory.

Checks cover six primary destinations in light/dark mode at 810x470 and 1280x800, top-navigation clearance and usable content height,
account-popup opening after navigation, direct category/content settings layout, switching back to the first settings category,
vertical sidebar scrolling, separate category actions, account opening without navigation, pinned toolbar layout and nested wheel chaining, scroll restoration,
and responsive instance tile wrapping. Each main-page capture is rendered at 1x, 1.5x and 2x density.
The full run additionally opens all nine settings categories in Chinese and English, in both themes and window sizes.
It expands advanced cards, exercises the conditional personalization controls, and visits the entire scroll range
with overlapping viewport steps. A missing or incorrect scroll owner fails the run. Geometry checks cover
interactive overlaps, horizontal overflow and cropped checkbox/radio labels.

Off-screen fixtures use synthetic records to exercise pinned toolbars for worlds, datapacks, embedded mods,
favorites and servers, together with empty/loading states, measured selection-footer clearance,
responsive server cards and wrapping game-log actions. These fixtures do not modify real worlds or favorites.

Background HTTP/socket metadata error dialogs are cancelled only inside this isolated harness and recorded
as `INFO`; other unexpected modal dialogs fail the settings capture. This is not a network-availability test.

For a quick fixture-only diagnostic, create `lists-only.flag` beside the isolated executable. Remove it before
a full run. Normal builds do not read this flag or include any harness code.

These raster captures do not replace real monitor DPI or high-contrast testing.

Navigation and bottom-tab motion unit tests:

```powershell
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -p:IsTestProject=true --filter 'FullyQualifiedName~LauncherNavigationTest|FullyQualifiedName~LiquidTabMotion'
```

Actual Microsoft sign-in, game launch, downloads, cancellation, custom video backgrounds and OS
accessibility preference changes require a separate end-to-end pass with suitable test data.

For download-page regressions, create `downloads-only.flag` beside the isolated executable.
This checks resource item initialization for all resource types, both sources, all version/loader
description combinations and both themes, subtitle visibility, virtual row heights, empty results,
and navigation through every download category. Results are in `downloads-report.txt`.
Navigation uses live metadata sources; this check does not install or download game files.

For HTTP/DNS/TLS investigation, create `network-only.flag` beside the isolated executable.
The run writes `network-report.txt` under the same temporary report directory and exits.
It captures Modrinth availability, per-provider A/AAAA answers, system DNS answers, certificate chain errors and validity dates,
and compares NeoForge metadata requests with system DNS and the app's DoH callback.
TLS validation remains enabled. HTTP 400/502 on a bare `/dns-query` GET does not establish
whether that service supports valid DNS queries; use the separate A/AAAA probe results.

Create `modrinth-only.flag` for the resource-source integration pass. It checks removal of
the source selector and unsupported categories, blocking of legacy provider APIs before a request,
five live Modrinth search types, category/loader/version filtering, project files and dependencies,
and download page navigation. It fails if the run logs any removed API, mirror or expired DoH source.
Results are written to `modrinth-report.txt`; it does not download or install game/resource files.

Create `install-header-only.flag` to check the installation name/download row at 810px and
1280px window widths, in both themes with Chinese, English and German button labels.
It verifies spacing, alignment and clipping, saves header screenshots, and writes `install-header-report.txt`.

Create `background-only.flag` to exercise the background source selector with live landscape/anime
APIs, decoded PNG caching, theme/size layouts, timed-out refresh preservation, stale-request
cancellation, original colors and an isolated local-image fixture. It writes `background-report.txt`
and screenshots. Images are fetched only in this opt-in test or when an online source is selected.

Deterministic DNS fallback and provider failover tests:

```powershell
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -p:IsTestProject=true --filter FullyQualifiedName~DnsFallbackTest
```

Content-sized button audit (no application launch):

```powershell
python tools/fluent-ui-smoke/AuditButtonWidths.py
```

The smoke harness also measures Chinese, English and German button labels, and checks that a
changing progress label shrinks again. Standard text actions use 12px horizontal padding;
icon-only controls retain square hit targets. No per-frame width recalculation is needed.

Fixed-height container and row audit:

```powershell
python tools/fluent-ui-smoke/AuditLayoutConstraints.py
```

This checks all page XAML for fixed containers, wrap-panel rows and direct grid rows smaller than the
40 DIP minimum used by the shared text actions and editors. Runtime geometry checks remain necessary
for translated content and dynamically populated controls.


Create `frame-performance-only.flag` for the opt-in performance matrix (810/1280 DIP,
static wallpaper, glass on/off, idle, deterministic scrolling, dock drag, page changes
and resize). The result is `frame-performance-report.txt`. It records UI rendering
callback intervals (median/P95/P99), process CPU, managed allocation and layout events.
These callbacks are **not** GPU presents or displayed FPS; collect ETW/PresentMon on
the target monitor for a refresh-rate acceptance test. The capture itself forces
continuous callbacks only during active workloads; idle CPU is measured without one.
Add `frame-isolation.flag` to replace the matrix with the normal/no-blur/no-dock/
no-input/no-content breakdown. This deliberately changes appearance for diagnosis.

Create `frame-home-only.flag` for the homepage material A/B (810/1280 DIP, fixed local
wallpaper and scroll fixture, idle/scroll/dock drag, two alternating passes). Production
inherits `UseLightweightMaterial` from the main window; this isolated check sets it on the
Launch page roots so only the homepage is toggled. The dock stays enabled, samples
`SceneBackdrop`, and must retain its full graph in every group. The report also proves
lightweight surfaces have no effect/sampling graph or coordinate tracking, and saves an
original/lightweight screenshot per width in the QA root. As with the other frame modes,
these are callback intervals, not presented FPS.

Create `frame-pages-only.flag` to A/B the same inherited property on the main window
across Settings Launch, Personalization, Management and About. It scrolls each page at
810/1280 DIP with a fixed wallpaper and asserts visible page surfaces have no glass graph
or coordinate tracking in lightweight mode, restore their graphs in original mode, and
that both dock surfaces keep their full graph throughout. Screenshots are written beside
the report. These are callback intervals, not presented/displayed FPS.
`animation-scheduler-only.flag` verifies animation completion at 60/144/240/automatic,
same-name replacement, worker wake-up, loading visibility and idle detachment.
The normal launcher does not contain or read any of these diagnostic switches.

Create `slider-only.flag` for liquid slider gestures, spring cleanup, hardware glass
captures and expanded settings layout checks. See `LIQUID-GLASS.md` for coverage.

```powershell
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -p:IsTestProject=true --filter 'FullyQualifiedName~LiquidSliderMotion|FullyQualifiedName~LiquidTabMotion|FullyQualifiedName~LauncherNavigationTest'
```

Use `slider-fixture-only.flag` to run just the isolated slider gesture/optical fixture
without repeating the settings layout matrix. Track-sample captures must contain
nontransparent pixels; flat/pressed pairs allow direct inspection of refraction.
