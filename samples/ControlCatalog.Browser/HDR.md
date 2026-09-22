# Experimental Browser HDR PoC

This fork can reuse its existing HDR rendering infrastructure in Chromium without
a WebGPU backend or a GPU readback/upload bridge. The path is:

`Skia -> RgbaF16/ScRgbLinear -> WebGL2 RGBA16F/srgb-linear -> browser HDR compositor`

`BrowserPlatformOptions.PreferHdr` opts in; it defaults to `false`. The backend
requires `EXT_color_buffer_float`, `drawingBufferStorage`, linear sRGB output,
and either `canvas.configureHighDynamicRange({ mode: "extended" })` or the newer
`gl.drawingBufferToneMapping` API. Successful negotiation reports the actual
format through both the GL render target and drawing session, so the fork's
existing Skia/compositor intermediate surfaces also use floating-point color.
Missing APIs or rejected configurations fall back to the existing SDR format.

## Manual Check

1. Enable HDR in the operating system and use an HDR-capable display.
2. In a recent Chrome/Chromium, enable
   `chrome://flags/#enable-experimental-web-platform-features` and relaunch.
3. Open `http://127.0.0.1:5088/?HdrDemo=true&PreferHdr=true`.
4. Expect `RgbaF16/ScRgbLinear`, reference `1.000`, and highlight `4.000`.
   The right-hand patch should be brighter than the left. Its central rectangle
   stays at reference white. The 1x/2x/4x/8x ramp distinguishes highlight levels
   up to the display's available headroom.
5. Change the highlight slider, then uncheck **Request HDR output** to reload
   in SDR. Both white patches should collapse to `1.000`.

The explicit SDR URL is
`http://127.0.0.1:5088/?HdrDemo=true&PreferHdr=false`.
Without the browser flag, the HDR URL should say **SDR fallback**.
`RenderingMode=WebGL1` or `RenderingMode=Software2D` can also exercise fallback.

## Build And Serve

Run from the repository root with the repository's .NET SDK and WebAssembly
workload installed. The browser JavaScript dependencies must be installed first
using the existing browser project build tooling.

```sh
(cd src/Browser/Avalonia.Browser/webapp && node build.js)
dotnet build samples/ControlCatalog.Browser/ControlCatalog.Browser.csproj -c Release
DOTNET_USE_POLLING_FILE_WATCHER=1 ASPNETCORE_URLS=http://127.0.0.1:5088 \
  dotnet run --project samples/ControlCatalog.Browser/ControlCatalog.Browser.csproj \
  -c Release --no-build --no-launch-profile -- --urls http://127.0.0.1:5088
```

Restart the server after rebuilding: its static-asset manifest contains
fingerprinted WebAssembly filenames. Hard-reload an already-open page after a
rebuild if it retains the previous runtime manifest. Polling avoids exhausting
inotify instances on a busy Linux development machine.

Focused headroom/negotiation tests and TypeScript validation:

```sh
node --test src/Browser/Avalonia.Browser/webapp/tests/screens.test.cjs \
  src/Browser/Avalonia.Browser/webapp/tests/webGlRenderTarget.test.cjs
node src/Browser/Avalonia.Browser/webapp/node_modules/typescript/bin/tsc \
  --noEmit -p src/Browser/Avalonia.Browser/webapp/tsconfig.json
```

## Multithreaded WebAssembly

Chrome 156 is required for multithreaded WebAssembly to work. Enable both tone mapping and linear color spaces:

```text
--enable-features=WebGLToneMapping,ColorSpacePredefinedLinearSpaces
```

Alternatively, `--enable-experimental-web-platform-features` enables both in
that Canary build.

## Luminance And Headroom

Browser `srgb-linear` output is reference-white-relative: `1.0` is browser
white, not a guaranteed 80 nits. The Windows scRGB 80-nit convention must not
be inferred just because Avalonia reports `ScRgbLinear`.

To preserve the current API, the browser maps screen headroom into
`PlatformSurfaceColorVolume` using **effective**, not measured, luminance:

| Field | Effective value |
| --- | --- |
| `PrimaryLuminance` | 0 to 203 nits |
| `ReferenceWhiteNits` | 203 |
| `SurfaceNitsPerUnit` | 203 |
| `TargetLuminance` | 0 to `203 * 2^hdrHeadroom` nits |
| `Transfer` | `Linear` |

This keeps `ReferenceWhiteScale` at 1 and `HeadroomRatio` at `2^hdrHeadroom`.
For example, 0/1/2 stops map to effective peaks of 203/406/812 nits. The zero
minimum is a nominal black level, not a display black-level measurement.

Chrome Canary 156.0.8067.0 exposes experimental, permission-gated screen
headroom. Add `ScreenDetailedHdrHeadroom` to the existing feature list:

```text
--enable-features=WebGLToneMapping,ColorSpacePredefinedLinearSpaces,ScreenDetailedHdrHeadroom
```

Use **Read display headroom** in the PoC, or call the existing
`Screens.RequestScreenDetails()` API from a user action, to request
window-management permission. Previously granted permission is reused without
a prompt. HTTPS or localhost is required.

The top level exposes the mapping through `IPlatformSurfaceColorVolumeFeature`.
HDR drawing sessions and Skia leases receive the same synchronized snapshot
through `PreferredColorVolume`, including on the .NET render worker. Changes
to headroom, current screen, display membership, or permission update the
metadata and raise `PreferredColorVolumeChanged` on the UI thread. SDR render
sessions do not report an HDR color volume.

Without permission or the headroom feature, `PreferredColorVolume` remains
null. Negative, non-finite, or overflow-producing readings are also rejected.
Permission revocation clears the old volume rather than retaining stale values.
No permission prompt is triggered automatically by requesting HDR output.

No tested screen API reports absolute reference-white nits, peak nits, or
nits per surface unit. The values above are an explicit normalization convention
for existing consumers, not newly discovered physical measurements. Screen
headroom is also not a guarantee of a canvas's final luminance after CSS limits,
browser policy, and display processing. This change reports the effective volume;
it does not add application-side tone mapping or alter rendered pixel values.

For content with a known reference white, normalize linear light by that
content reference white. For example, the nominal HDR/PQ convention uses
203 nits: `surfaceValue = contentNits / 203`, followed by any chosen tone mapping
to the available relative headroom. A float16 `ImageData` conversion test in
Canary confirmed 80/203/406/812-nit PQ samples map to approximately
0.394/1/2/4 in `srgb-linear`. Treat the browser's reported `SurfaceNitsPerUnit`
as this effective content scale, never as calibrated display luminance.

`drawingBufferToneMapping` currently accepts only `standard` and `extended`.
Extra luminance/headroom dictionary fields are silently ignored. The proposed
Reinhard and SMPTE ST 2094-50 modes were rejected in the tested build; mastering
metadata is not a display-luminance query either. Exact physical luminance still
requires calibration or a native host reporting actual display parameters.

## Scope And Limitations

- This is an experimental WebGL2 PoC, not a cross-browser HDR guarantee.
  Older Chromium versions may support TestUFO's other color spaces but not
  `srgb-linear`; this PoC deliberately falls back in that case.
- Browser/GPU/OS/display support is still required for visible HDR. On Linux,
  use a hardware-accelerated, color-managed Wayland browser session. Checking
  `matchMedia('(dynamic-range: high)').matches` is useful but does not measure
  brightness or prove that this particular canvas is being presented in HDR.
- Values are multiples of browser reference white, not calibrated nits. The
  reported color volume uses nominal 203-nit white and the browser's current
  headroom; absolute display luminance is still unavailable.
- The probe uses Skia gradients with equal floating-point stops, following the
  existing `WideColorGamutDemo`. Ordinary Avalonia byte colors remain SDR;
  constant Skia paint colors can clamp above-white values.
- The readback measures the rendered Skia surface, not emitted display light.
  Normal PNG screenshots also cannot demonstrate HDR brightness. The headless
  software renderer used during development failed to present even a minimal
  float16 WebGL canvas, whereas GPU-backed Wayland Chromium rendered this PoC.
- Verified with Chromium 153 on AMD/Wayland: 16-bit linear sRGB, 1.0 reference
  white, and 4.0 highlight in the single-threaded sample. Multithreaded HDR is
  verified with Canary 156 and the flags above.

## References

- [TestUFO HDR](https://testufo.com/hdr) uses floating-point WebGL storage and
  extended canvas tone mapping for its WebGL HDR path.
- [Chromium WebGL API definitions](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/modules/webgl/webgl_rendering_context_base.idl)
- [Chromium predefined color spaces](https://github.com/chromium/chromium/blob/main/third_party/blink/renderer/core/html/canvas/predefined_color_space.idl)
- [WebGL tone-mapping proposal](https://github.com/w3c-cg/ColorWeb-CG/blob/main/webgl-drawing-buffer-tone-map.md)
- [Chrome WebGL tone-mapping feature status](https://chromestatus.com/feature/5424242161221632)
- [Chrome's separate WebGPU HDR path](https://developer.chrome.com/blog/new-in-webgpu-129#hdr_support_with_canvas_tone_mapping_mode)
- [Chromium ScreenDetailed headroom implementation](https://github.com/chromium/chromium/blob/156.0.8067.0/third_party/blink/renderer/modules/screen_details/screen_detailed.cc)
- [ScreenDetailed HDR headroom proposal](https://github.com/w3c/window-management/issues/149)
- [CSS HDR reference-white and headroom definitions](https://drafts.csswg.org/css-color-hdr/#defining-dynamic-range)