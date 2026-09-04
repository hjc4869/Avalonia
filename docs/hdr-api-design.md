# HDR surface API

The HDR API separates optional luminance information, relative headroom, and tone-mapping
responsibility. Applications render extended-linear floating-point pixels and select rendering
policy from the negotiated surface contract, without branching on the operating system.

These APIs are unstable. They do not promise source or binary compatibility.

## Surface contract

`PlatformSurfaceColorFormat` describes the actual pixel representation. The logical HDR drawing
surface is `RgbaF16/ScRgbLinear`: linear sRGB primaries with meaningful negative and above-one
components. Native Wayland PQ presentation uses a retained FP16 drawing layer and a final backend
conversion to PQ. Applications use the format and volume from their rendering session or Skia
lease, not the requested startup format.

`IPlatformSurfaceColorVolumeFeature.PreferredColorVolume` reports the presentation contract for
a top level. Null means no report, not necessarily an SDR display. A non-null report can contain
unknown measurements while still describing a known white scale or tone-mapping policy.

```csharp
public readonly record struct PlatformLuminanceRange(
    double? MinimumNits,
    double? MaximumNits);

public enum PlatformToneMappingMode : byte
{
    Unknown,
    Client,
    Platform
}

public enum PlatformLuminanceBasis : byte
{
    Unknown,
    DisplayReported,
    Nominal
}

public readonly record struct PlatformSurfaceColorVolume(
    PlatformLuminanceRange? PrimaryLuminance = null,
    double? ReferenceWhiteNits = null,
    PlatformLuminanceRange? TargetLuminance = null,
    PlatformTransferFunction Transfer = PlatformTransferFunction.Unknown,
    double TransferExponent = 0,
    double? SurfaceNitsPerUnit = null)
{
    public double? ReferenceWhiteScale { get; init; }
    public double? HeadroomRatio { get; init; }
    public double? MaximumHeadroomRatio { get; init; }
    public PlatformToneMappingMode ToneMapping { get; init; }
    public PlatformLuminanceBasis LuminanceBasis { get; init; }
}
```

| Field | Meaning |
| --- | --- |
| `PrimaryLuminance` | Native encoding/reference range, if specified. Not panel capability or an extended-linear clipping limit. |
| `ReferenceWhiteNits` | Native diffuse-white luminance, qualified by `LuminanceBasis`; null if absent. |
| `TargetLuminance` | Native target or capability luminance, qualified by `LuminanceBasis`; null if absent. It need not be the current brightness grant. |
| `SurfaceNitsPerUnit` | Fixed or reported absolute input scale where one exists. Null for relative-white paths. An encoding scale is not a display measurement. |
| `ReferenceWhiteScale` | Diffuse white in extended-linear drawing units, independently reported. Relative-white paths use 1 without needing absolute nits. Not a code value in a nonlinear native PQ buffer. |
| `HeadroomRatio` | Current reported or target-derived headroom relative to diffuse white. Null means unknown; 1 means no current highlight headroom. A nominal-target-derived ratio is not calibrated physical headroom. |
| `MaximumHeadroomRatio` | Valid native potential headroom, which may exceed current headroom. Falls back to `HeadroomRatio` when no separate valid maximum is available; null only when neither is available. Not a current render limit or a promise that a hint obtains it. |
| `ToneMapping` | Actual highlight-mapping responsibility of the selected path. |
| `LuminanceBasis` | Qualifies white/target nits: `DisplayReported` for native display/target information, `Nominal` for theoretical/reference targets, `Unknown` when neither is available. |
| `Transfer`, `TransferExponent` | Preferred/native encoding information. `Power` has a positive exponent; other transfers use exponent 0. Unknown transfer does not imply unknown headroom. |

Backend reports are normalized: measurements must be finite, minima nonnegative, positive scales
and maxima positive, and headroom ratios at least 1. Invalid endpoints are removed independently.
A minimum greater than a valid maximum is discarded; an empty range becomes null. A potential
ratio smaller than a valid current ratio is discarded. A missing or discarded maximum then falls
back to the valid current ratio. An independently reported valid maximum is preserved, including
when current headroom is unknown. Unknown luminance basis suppresses white/target nits. Unrelated
valid information and mapping policy are retained.

Equal current and maximum ratios do not prove that the display cannot provide additional headroom:
the maximum may simply be the current-ratio fallback. Both values can change with native feedback.

`DisplayReported` is not a calibration certificate. Driver data, display brightness, automatic
brightness limiting and window overlap affect emitted light. `Nominal` must not be presented as
physical display luminance. A nominal target can supply a client-rendering ratio for Wayland's
relative intent, but it is not a measurement of physical current headroom. An advertised encoding
maximum alone is insufficient to derive presentation headroom.

For a fixed absolute mapping with matching native reports, white scale is
`ReferenceWhiteNits / SurfaceNitsPerUnit`. Windows HDR derives current headroom from matching
target peak and native white. Relative-only platforms report ratios directly. No backend
manufactures absolute nits from a dimensionless ratio or substitutes potential for current headroom.

Wayland with relative intent derives `HeadroomRatio = max(1, TargetLuminance.MaximumNits /
ReferenceWhiteNits)` when both inputs from the same preferred-feedback snapshot are finite and
positive, and the ratio is finite. Neither an encoding range nor a minimum luminance is required.
Missing/invalid inputs or an overflowing ratio leave it null. Perceptual intent does not derive
this ratio. The feedback's nominal luminance basis is retained; this rule does not infer physical
display white or change the input scale of fixed Windows-scRGB surfaces.

## Rendering policy

- `Client`: fit highlights to the current target. Color conversion and compositing can occur,
  but suitable highlight roll-off is not promised; out-of-range values may be clipped.
- `Platform`: the selected compositor path adapts highlights. Submit content in the declared
  linear white scale without also compressing it to the display peak. The curve and quality are
  platform-defined; this is not a lossless or reference-display guarantee.
- `Unknown`: use a deliberate SDR or user-configured policy; do not assume passthrough or infer
  ownership from the OS name.

An FP16 buffer, color management, or an HDR content hint alone does not establish platform tone
mapping. Browser `extended` canvas mode permits clamp/projection and therefore reports `Client`.
Wayland relative colorimetric intent reports `Client`; perceptual intent reports `Platform`.

Application flow:

1. Declare HDR intent from visible source content, before waiting for headroom.
2. Observe the top-level change event and use the drawing session's coherent snapshot per frame.
3. Use the actual format. Non-extended surfaces use the SDR rendering path.
4. For `Platform` with a known white scale, preserve source highlights in that scale.
5. For `Client`, use `HeadroomRatio`, including a current ratio of 1 while waiting for resources.
   If it is unknown, choose SDR at the known white scale or an explicit user target.
6. When white scale is unknown, do not claim reference-white matching. Prefer a supported
   relative-white/SDR presentation path for general UI; raw absolute-input applications may
   deliberately use a known `SurfaceNitsPerUnit` instead.

The Skia backend uses `ReferenceWhiteScale` for ordinary color conversion into extended-linear
surfaces on every platform. Its conservative drawing fallback for unknown scale is 1; this does
not convert an unknown mapping into a measured white. Custom draws must avoid applying this scale
twice when Skia color conversion already applies it.

Decode source material using its source transfer function, not the output's `Transfer`.
For relative presentation, an absolute source can be normalized by its own declared content white
(for example `contentNits / contentReferenceWhiteNits`). That content convention does not establish
display white. Do not PQ-encode pixels written into an extended-linear lease.

## Content hints and updates

```csharp
public readonly record struct PlatformHdrContentMetadata(
  double? HeadroomRatio = null,
  double? ReferenceWhiteNits = null,
  PlatformLuminanceRange? MasteringLuminance = null);

public interface IPlatformHdrContentFeature
{
  void SetHdrContent(bool hasHdrContent, PlatformHdrContentMetadata? metadata);
}
```

This optional top-level feature accepts a resource hint on the UI thread. The default is false.
Aggregate visible HDR views, set true before waiting for a grant, and clear it when the source
content disappears or is deliberately switched to SDR. Temporarily fitting HDR into a one-times
target while waiting must not clear the hint and prevent activation.

There is one metadata-bearing method, with no boolean-only overload or compatibility shim.
`SetHdrContent(true, null)` declares HDR with unknown source metadata; false clears metadata even
if an argument is supplied. Metadata-only changes take effect while the intent remains true.
Invalid fields are normalized independently using the same finite/range rules as output reports.

| Source field | Meaning |
| --- | --- |
| `HeadroomRatio` | Peak of the composed source relative to its diffuse white, not current or potential display headroom. |
| `ReferenceWhiteNits` | Authored source white, not a measurement of the display. |
| `MasteringLuminance` | Source mastering range. It is not a request to set display black or peak luminance. |

Aggregate metadata for the composed top-level content in one consistent white scale. A video's
mastering data alone need not describe a composite containing other content. These surface-state
hints are not a frame-indexed dynamic-metadata API; the backend may apply them before the next
pixel update. They do not change mapping ownership, pixel encoding, or reported display values.

The hint neither toggles display-wide HDR settings nor promises headroom. Supported usage is:

| Backend | Metadata handling |
| --- | --- |
| Android | `HeadroomRatio` requests native desired headroom, capped at 10,000. Unknown ratio uses automatic selection (0); false requests 1. Source white/mastering nits are retained but not used by this path. |
| Wayland parametric linear/PQ | When mastering metadata is supported, create a per-surface description with source mastering range and content light level in the native encoding's white scale. Preserve the drawing format and selected intent. |
| Wayland Windows-scRGB | Fixed native description; source metadata is retained but cannot alter its range. |
| Windows/browser and other backends without the optional feature | No content-metadata submission. Rendering policy still follows the surface report. |

Wayland rescales source mastering nits by native reference white divided by source reference
white: 80 for parametric linear, 203 for PQ. Without source white, absolute mastering values cannot
be rescaled. Content headroom independently supplies the native content light level, and supplies
the target peak when no rescalable mastering peak exists. An unknown mastering minimum uses the
native description's zero-black convention, not a claimed display measurement. Native maxima are
rounded up to integer nits and minima down to 0.0001 nits. Unsupported/unrepresentable metadata
(including a target above the supported 10,000-nit description range), or a rejected description,
uses the prepared default description. No metadata means the prepared SDR/HDR descriptions for
parametric linear and the fixed default description for PQ. Metadata-bearing updates require a
description-creation round trip; old per-surface descriptions are released when replaced.

Intent and metadata are retained across native surface recreation/reconnect where supported. Native feedback,
not accepting the hint, changes reported measurements. `PreferredColorVolumeChanged` runs on the
UI thread when the snapshot changes; rendering is invalidated and retained composition layers
are recreated when their color-volume snapshot changes. Display migration, configuration,
permissions, surface lifecycle and asynchronous native headroom updates can all cause changes.
See [HDR content hints](hdr-content-hints.md) for backend details.

## Wayland presentation preferences

`WaylandPlatformOptions.HdrPresentationPreferences` is an optional ordered list. It selects
complete supported transport/intent combinations, not independent settings that could conflict.

```csharp
var options = new WaylandPlatformOptions
{
    HdrPresentationPreferences = new[]
    {
        WaylandHdrPresentationMode.LinearPerceptual,
        WaylandHdrPresentationMode.PqPerceptual,
        WaylandHdrPresentationMode.Sdr
    }
};
```

| Candidate | Native transport | Mapping |
| --- | --- | --- |
| `LinearRelative` | Parametric FP16 extended linear | Client |
| `LinearPerceptual` | Parametric FP16 extended linear | Platform |
| `WindowsScRgbRelative` | Fixed 80-nit Windows-scRGB | Client |
| `WindowsScRgbPerceptual` | Fixed 80-nit Windows-scRGB | Platform |
| `PqRelative` | PQ, 10-bit preferred with FP16 fallback; linear drawing layer | Client |
| `PqPerceptual` | PQ, 10-bit preferred with FP16 fallback; linear drawing layer | Platform |
| `Sdr` | Untagged standard output | No HDR contract; stops negotiation |

A non-null list overrides `ColorMode` for color selection. Empty means SDR. Null uses `ColorMode`;
`ExtendedLinear` tries linear relative/perceptual, Windows-scRGB relative/perceptual, then PQ
relative/perceptual, in that order. Unsupported features/intents, unavailable EGL formats and
rejected image descriptions advance to the next candidate. SDR is an implicit final fallback.
The selected mode and fallback reasons are logged under `Wayland`.

These are startup preferences, retried when the connection is recreated, not guaranteed modes or
display-setting overrides. HDR is implemented for the WSI rendering path; the dmabuf rendering
path remains SDR. Windows and Android retain their `ColorMode` opt-ins, and browser retains
`PreferHdr`. No unsupported native transports or Apple HDR modes are exposed as options.

## Platform values

Tables describe the implemented logical drawing contract. Null means unavailable. Valid native
luminance endpoints are independent. Native capabilities are not instantaneous photometry.

| Environment | `PrimaryLuminance` | `ReferenceWhiteNits` | `TargetLuminance` | `LuminanceBasis` | `SurfaceNitsPerUnit` |
| --- | --- | --- | --- | --- | --- |
| Windows scRGB, HDR active | Encoding reference 0..80 | `DISPLAYCONFIG_SDR_WHITE_LEVEL * 80 / 1000`, if available | `DisplayMonitor` min/max nits, if available | `DisplayReported` when white/target exists | 80 |
| Windows SDR/WCG output | null | null | null | `Unknown` | null |
| Android extended-linear EGL, API 35+ | EGL encoding reference 0..80 | null | Valid `HdrCapabilities` desired min/max; optional and independent of the current ratio | `DisplayReported` for valid target, else `Unknown` | null |
| Browser extended canvas | null | null | null | `Unknown` | null |
| Wayland managed surface | Native preferred `luminances` min/max, if received | Native preferred reference luminance, if received | Native preferred `target_luminance`, if received | `Nominal` for received white/target, else `Unknown` | 80 for Windows-scRGB; null for relative linear/PQ composition |
| macOS / iOS / iPadOS | No HDR report | No HDR report | No HDR report | No HDR report | No HDR report |
| X11/XWayland, framebuffer/DRM, unmanaged Wayland and other unsupported paths | No HDR report | No HDR report | No HDR report | No HDR report | No HDR report |

| Environment | `ReferenceWhiteScale` | `HeadroomRatio` | `MaximumHeadroomRatio` | `ToneMapping` | Updates |
| --- | --- | --- | --- | --- | --- |
| Windows HDR scRGB | Native white / 80, or null | `max(1, target peak / native white)` when both exist | Same as current ratio | `Client` | Window/display changes and HDR/SDR-white state polling |
| Windows SDR/WCG | 1 | 1 | 1 | `Client` | Display configuration |
| Android API 35 | 1 | Availability-gated `Display.HdrSdrRatio`, or null | Same as current ratio | `Client` | Ratio listener, display/configuration changes, surface lifecycle |
| Android API 36+ | 1 | Same current ratio | Valid availability-gated `Display.HighestHdrSdrRatio`; otherwise current ratio | `Client` | Same lifecycle; maximum refreshed with current report |
| Browser extended canvas | 1 | `2^ScreenDetailed.hdrHeadroom` for a valid permission-gated native reading, else null | Same as current ratio | `Client` | Current screen, headroom, permission and initial canvas negotiation; worker-safe snapshots |
| Wayland reference-white-relative linear/PQ | 1 | Relative intent: `max(1, target peak / reference white)` with valid feedback; otherwise null. Perceptual: null | Same as current ratio | Relative intent: `Client`; perceptual: `Platform` | Preferred-description completion, surface lifecycle and reconnect |
| Wayland fixed Windows-scRGB | null; no measured diffuse-white mapping | Same relative/perceptual rule as above | Same as current ratio | Relative intent: `Client`; perceptual: `Platform` | Same as above |
| macOS / iOS / iPadOS and unsupported paths | No HDR report | No HDR report | No HDR report | No HDR report | No HDR feature implementation |

Browser SDR fallback reports no HDR volume, even on an HDR-capable screen. Permission loss clears
headroom but retains a negotiated extended canvas's known scale and mapping policy. A reported
screen ratio is not proof of final canvas luminance: browser/CSS limits and display processing
can further restrict output. The experimental browser property uses log2 stops; invalid or
overflowing readings remain unknown. Negotiating HDR does not prompt for screen permission.

Wayland's protocol permits theoretical target luminance. Even when KDE supplies plausible nits,
the generic backend labels them `Nominal`. Relative intent can use their peak-to-white ratio for
client highlight fitting, with the same ratio as the maximum fallback; physical current/potential
headroom remains unmeasured. Perceptual intent leaves both ratios unknown.
ICC-only, incomplete or failed feedback does not erase a successfully established surface policy.
Windows-scRGB's suggested nominal white is not treated as a measured diffuse-white mapping.

Android checks ratio availability before reading: the native APIs otherwise return a misleading
1. It does not require valid nits to expose a valid ratio, use the EGL studio white as phone white,
or calculate a requested/observed ratio from desired peak nits. API 34 ratio availability alone
does not enable the API-35-gated Avalonia presentation path.

### Transfer information

| Environment | `Transfer` | `TransferExponent` |
| --- | --- | --- |
| Windows HDR active | `Pq`, describing output, not the linear drawing input | 0 |
| Windows SDR/WCG | `Srgb` | 0 |
| Android | Mapped preferred wide-gamut color space, otherwise `Unknown` | Native/known exponent for `Power`; 0 otherwise |
| Browser extended canvas | `Linear`, the browser-side encoding, not a panel EOTF measurement | 0 |
| Wayland | Mapped `tf_named`, or `Power` from `tf_power`; absent/ICC-only is `Unknown` | Protocol exponent / 10000, or known named power; 0 otherwise |
| Apple and unsupported HDR backends | No HDR report | No HDR report |

## Demo and validation

The [native probe](../samples/WideColorGamutDemo/Program.cs) displays mapping ownership, current
and potential ratios, nominal/unknown luminance, and the actual drawing format in console
diagnostics. Controls select visible HDR source content, content peak, and an explicit client
target. Unknown client headroom defaults to SDR. An explicit target does not overwrite platform
feedback, and is ignored for platform mapping or unknown white scale.
The generated probe content declares a 203-nit source white and submits its content peak/mastering
range. Changing the content slider updates metadata without toggling HDR intent. This authored
203-nit convention is not reported as physical display white.

```sh
dotnet run --project samples/WideColorGamutDemo -c Release -- --hdr=LinearPerceptual
dotnet run --project samples/WideColorGamutDemo -c Release -- --hdr=PqRelative --self-test
dotnet run --project samples/WideColorGamutDemo -c Release -- --hdr=Sdr,LinearPerceptual --self-test
```

`--hdr=` selects an empty list; `extendedlinear` selects the default HDR order. `--dmabuf` exercises
the SDR dmabuf path. `--self-test` changes controls through seven timed steps, verifies reference
and peak values in the drawing surface, and closes with a nonzero exit code on a failed or missing
set of checks. Negative wide-gamut values are included in the diagnostic readback.

Live validation on KDE/KWin 6.7.4, Mesa 26.1.6, AMD Radeon 8060S, with HDR already enabled:

- All six transport/intent candidates negotiated and passed the hint-transition pixel checks.
- PQ selected `Rgba1010102/Rec2020Pq` natively while drawing remained `RgbaF16/ScRgbLinear`.
- Protocol traces confirmed relative/perceptual intents, metadata-only description updates for
  linear/PQ, and clearing back to the prepared descriptions. The 8x/12x source peaks generated
  640/960-nit linear and 1624/2436-nit PQ mastering/light-level metadata. Windows-scRGB stayed fixed.
- Explicit preference ordering, empty lists, SDR termination, the default HDR order and the
  dmabuf SDR path passed. No global display settings were changed.
- Full Skia and Wayland unit suites passed. Android and browser/sample builds passed. Windows
  conversion assertions passed on Linux outside the Win32-only UI integration-test harness.

This validates negotiation, reporting, input pixels and protocol behavior, not calibrated emitted
luminance or perceptual mapper quality. SDR screenshots cannot prove HDR brightness. Physical
Android/Windows display transitions, compositor reconnects and multi-monitor behavior still need
device validation; Apple HDR is not implemented.

## Native references

- [Windows Advanced Color and application tone mapping](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range).
- [Android Display ratios](https://developer.android.com/reference/android/view/Display),
  [HDR capabilities](https://developer.android.com/reference/android/view/Display.HdrCapabilities), and
  [SurfaceView headroom requests](https://developer.android.com/reference/android/view/SurfaceView#setDesiredHdrHeadroom%28float%29).
- [EGL extended-linear reference encoding](https://registry.khronos.org/EGL/extensions/EXT/EGL_EXT_gl_colorspace_scrgb_linear.txt).
- [Wayland color-management protocol](https://gitlab.freedesktop.org/wayland/wayland-protocols/-/blob/main/staging/color-management/color-management-v1.xml).
- [Canvas clamp/projection and tone-mapping definitions](https://github.com/w3c-cg/ColorWeb-CG/blob/main/canvas-tone-map.md),
  [WebGL binding](https://github.com/w3c-cg/ColorWeb-CG/blob/main/webgl-drawing-buffer-tone-map.md), and
  [experimental screen headroom](https://github.com/w3c/window-management/issues/149).