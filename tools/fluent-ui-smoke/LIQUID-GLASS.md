# Liquid Glass implementation and validation

The launcher keeps WPF and adapts the rounded lens math from AndroidLiquidGlass
`kmp` commit `65ab177e90e5c1d8c62e70cf7755841982da65f6`. Attribution and the
Apache-2.0 license are under `PCL.Core/UI/Assets/Shaders/` and copied to published output.

The bottom bar additionally ports the upstream spring/gesture model and press-driven
material. See [DOCK-PARITY.md](DOCK-PARITY.md) for source links, exact parameters,
validation and platform-specific rendering boundaries.

## Rendering contract

`LiquidGlassSurface` extends `Border`, so existing padding, child layout, background
animations and hit targets remain compatible. Foreground content is drawn by WPF
after the background lens. The shared card, button, editor, dropdown, popup and
instance-tile implementations use this surface. Ordinary `Content`, `Chrome`, `Action`
and `Input` instances now use the lightweight material described below; the dock and
floating popups keep the full lens pipeline.

- Set the inheritable attached `BackdropSource` to a **separate background visual**.
  Never use a visual containing the glass surface. The main window registers
  `LiquidBackdrop`, containing the default anime wallpaper (gradient while loading/offline) and existing image/video canvas.
  The topmost navigation instead samples its sibling `SceneBackdrop`, containing
  that background **and the actual page content**, so scrolling text remains visible
  through its glass. The sampled scene never contains the navigation itself.
- `Material` selects Content, Chrome, Action, Input or Floating treatment.
  `RefractionStrength` accepts 0–2; zero is useful for comparison with a flat material.
  `CornerRadius`, `Background`, `BorderBrush`, `Padding` retain their usual meaning.
- `BlurRadius` accepts 0–64 DIP. The launcher theme connects it to the existing blur
  preference, with a 32 DIP application limit. Inputs share the card blur to avoid sharp wallpaper cutouts. The
  dedicated bottom-tabs presets use the upstream fixed 8 DIP blur; OS accessibility
  preferences still select the opaque fallback.
- The pipeline samples an inflated source region, blurs, applies the rounded lens,
  composites bounded neutral/semantic tint and directional highlights. Foreground contrast uses explicit tint, with no per-pixel brightness clamp or general-card saturation boost.
  Sampling uses public WPF visual transforms and brushes; popup coordinates cross
  HWND boundaries through screen coordinates.
- Static source visuals are retained by WPF. Layout changes invalidate only when
  source coordinates or dimensions actually change. Video/animated backgrounds
  update through the retained visual brush without a per-surface frame timer.
  Unloading removes layout subscriptions and releases surface-held visual caches.
- High contrast, disabled Windows transparency, unsupported shader hardware and
  absent sources use an opaque fallback. `GlassEnabled=false` exercises this path
  without changing Windows preferences. Reduced animation follows existing controls'
  `ClientAreaAnimation` checks.

Compile shader changes with:

```powershell
& PCL.Core/UI/Assets/Shaders/CompileLiquidGlass.ps1
```

The committed `.ps` is embedded in PCL.Core; ordinary builds do not need FXC.

## Lightweight page material

`FormMain` sets the inheritable `UseLightweightMaterial` attached property to `true`.
When a surface requests it and uses `Content`, `Chrome`, `Action` or `Input`, the surface
draws a rounded theme tint and border without the per-control lens shader. `Content`
and `Chrome` cards additionally sample one shared, live background at half resolution
per background/radius/kernel combination. Its cached blur is capped at 12 DIP (6 pixels
at half resolution); cards crop that shared visual using their background coordinates.
One `BitmapCacheBrush` reuses the target visual's cache across cards; cards only apply
a crop transform and rounded clip, with no brush-local cache. This follows the WPF
[cached element reuse contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapcachebrush?view=windowsdesktop-10.0).
The source viewbox is explicit, and stretched edge samples pad the shared source before
blur. A 16-pixel cache gutter covers the blur/filter support; card coordinates account
for that gutter. Transparency at the blur boundary must not mix the dark fallback into
panels near a window edge. Uniform-input sidebar captures check interior edges as well
as the exterior shadow region.
Flat blur composites directly over the scene: it must not place an opaque
`FallbackBrush` beneath the blurred cache. Cache transparency would otherwise reveal
that fallback as a dark inner band (or a pale band in light mode). Opaque accessibility
fallbacks remain in the disabled-glass path.
Foreground children are never part of that cache. There are no screenshot readbacks,
frame timers, or per-card blur effects. Coordinate tracking runs only for cards needing
the blur; `Action` and `Input` washes keep their direct drawing path. Weak cache entries
and unloading release card-held caches. Wallpaper/video changes propagate through the
retained visual, while scrolling changes the crop. With blur disabled, software rendering,
or no valid source, cards retain the readable flat tint. Blurred cards use a lighter tint
to expose the softened wallpaper without blurring text.

Ordinary `MyCard` surfaces have no outer shadow, including on hover. Their legacy
shadow slot stays collapsed to preserve custom-card folding indices and API callers.
The 1 DIP outline is a faint light gray in dark mode and dark gray in light mode;
there is no black rim, bevel or lens shader in the lightweight card path.
`Floating` popups and both `BottomTabs`/`BottomTabIndicator` materials never enter this
path, even when the inherited value is set. Pages can opt out locally with
`UseLightweightMaterial="False"` if a surface genuinely needs the full lens.
The dock continues to sample `SceneBackdrop`; it is not frozen or replaced by a snapshot.

## Layout changes

`ResponsiveColumnsPanel` gives the launch action area, recent/tasks cards and
theme/accent settings independent rows when their columns cannot fit. The navigation
overlays the full-height page at the highest sibling Z order. No bottom spacer or
navigation clearance is added to page viewports, fixed actions or the sidebar.
The home header no longer receives a second full-width category glass layer.

## Reproduce validation

Build the isolated executable using `Inject.targets` as described in `README.md`.
Create `glass-only.flag` beside it for the material pass; remove the flag for the
full navigation, localization, toolbar and settings pass. Configuration and test
instances remain under `%TEMP%/pcl-ui-efficiency-integration`.

Optional video fixture (FFmpeg):

```powershell
ffmpeg -f lavfi -i testsrc2=size=640x360:rate=30 -t 3 -c:v libx264 -pix_fmt yuv420p "$env:TEMP/pcl-liquid-fixture.mp4"
```

Material checks compare hardware screenshots with refraction enabled and disabled,
away from text/highlights; check idle invalidation; open a separate-HWND popup;
exercise asymmetric corners, light/dark themes, narrow/wide windows, a high-contrast
wallpaper and an optional real MediaElement video. CPU/private memory and WPF
composition callback intervals are diagnostics, **not GPU presentation benchmarks**.

`RenderTargetBitmap` does not execute WPF ps_3_0 effects. The full harness retains
its 100/150/200% raster layout captures and additionally saves `material-*.png`
HWND captures for glass appearance. Actual monitor DPI, high contrast, disabled
system transparency and reduced motion still need OS-level checks on target machines.
Real Microsoft sign-in/game launch/download completion require suitable account
and instance data; layout fixtures do not establish those end-to-end outcomes.

Build outputs, screenshots and the session validation report are saved under the
ignored `artifacts/liquid-glass/` directory; they are not part of the source patch.

## Liquid sliders

All launcher value sliders share `MySlider`: 6 DIP capsule track, 40x24 DIP thumb,
1.5 pressed scale, upstream value/velocity/press and X/Y scale springs. The
`SliderThumb` material always uses the full glass pipeline, including when pages
inherit `UseLightweightMaterial=true`. A sibling-only track VisualBrush is composed
with the inherited background before the press-dependent blur and lens. Idle thumbs
have the upstream white surface; disabled and accessibility states retain a clear
outline and opaque fallback. The existing theme accent colors the completed track.

The WPF adapter retains integer values, keyboard steps, preview rejection, change
callbacks and hint delegates. Value updates are immediate; only visual state follows
the spring. Reduced motion snaps to values. Capture loss, disabling, hiding, unload
and range changes release pointer ownership and close hints. Compositor subscriptions
end when springs settle; hint timeout timers stop on close/unload.

Create `slider-only.flag` beside the isolated executable to run the continuous-slider
interaction fixture followed by all settings layouts. The fixture checks range and
keyboard bounds, rejected changes, drag/reversal, zero/narrow ranges, disable/unload
and idle cleanup. It writes `slider-report.txt` under the isolated QA root, hardware
captures for rest/pressed/flat/fallback in both themes and 1/1.5/2x raster layout
captures. The settings pass expands conditional background/volume controls and audits
both themes, 810/1280 DIP windows and English/Chinese labels. Hardware captures are
required for optical inspection; raster captures establish layout only.
