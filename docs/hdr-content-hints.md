# HDR content hints

For nullable luminance, headroom and tone-mapping semantics, see the
[HDR surface API](hdr-api-design.md).

`IPlatformHdrContentFeature` separates an HDR-capable rendering surface from a request for HDR
headroom. The surface can remain extended-linear while displaying SDR content, without requesting
extra display brightness just because the application started.

Call the optional feature on the UI thread when visible content needs luminance above reference
white. Reset it when that source content is removed or deliberately switched to SDR. Temporarily
fitting HDR into a one-times target while waiting for a grant must not clear the hint. The default
is false. Applications with multiple HDR views must combine their requests and source metadata
for the same top level.

```csharp
using Avalonia.Platform;

var feature = topLevel.PlatformImpl?.TryGetFeature<IPlatformHdrContentFeature>();
feature?.SetHdrContent(true, new PlatformHdrContentMetadata(
  HeadroomRatio: 8,
  ReferenceWhiteNits: 203,
  MasteringLuminance: new PlatformLuminanceRange(0, 1624)));
feature?.SetHdrContent(false, null);
```

Metadata describes source content, not display luminance or a granted ratio. Pass null metadata
when it is unknown. False clears stored metadata. Updating metadata while true remains true is
supported; native backends use only the fields their presentation path supports.

This is an application-supplied hint, not automatic pixel analysis. Do not wait for the currently
reported headroom to exceed 1 before setting it: the platform may need the hint to make that
headroom available. Continue using `IPlatformSurfaceColorVolumeFeature` and its change event to
adapt rendering when current headroom is reported. `MaximumHeadroomRatio` uses a valid native
potential ratio when available, otherwise it falls back to the current ratio. It is not a resource
grant or a render limit; equality with current headroom does not prove that more is impossible.
Unknown ratios remain null. Other visible HDR surfaces and system policies can affect the result.

## Android

The feature is available when `AndroidColorMode.ExtendedLinear` successfully negotiates FP16 scRGB
through EGL on Android 15 (API 35) or newer. Standard, software, Vulkan, and unsupported-device
fallbacks do not expose it.

The backend uses the public
[SurfaceView.SetDesiredHdrHeadroom](https://learn.microsoft.com/en-us/dotnet/api/android.views.surfaceview.setdesiredhdrheadroom)
API on Avalonia's own surface:

- False requests `1.0`, which means no HDR headroom. `0.0` would instead restore Android's automatic
  selection and could engage HDR without any HDR content.
- True requests the supplied content `HeadroomRatio`, capped at the native limit of 10,000.
  If unknown, it requests `0.0`, Android's automatic selection. No request is calculated from
  desired display peak nits or an assumed SDR white. Source mastering/white nits are not consumed
  by this native path.

The hint and metadata are retained across native surface recreation and reapplied on surface creation and display
or configuration changes. It does not recreate EGL buffers, change their color space, set activity
window color mode, or override the user's brightness setting. The surface API is independent of
`Window.SetDesiredHdrHeadroom`, which does not control `SurfaceView` layers. Actual headroom remains
subject to Android's display, ambient-light, and power policies.

Current headroom comes from availability-gated `Display.HdrSdrRatio`; API 36+ additionally reports
`HighestHdrSdrRatio` as potential headroom. If a separate valid maximum is unavailable, maximum
headroom equals the current ratio. Nits are not required to expose these ratios. An invalid or
unavailable current measurement remains null rather than becoming a known one-times target.

## Wayland

Parametric linear/PQ descriptions can carry source mastering luminance and content light level.
The backend rescales authored nits into the selected description's reference-white scale, keeping
the existing extended-linear drawing input. Metadata-only changes replace the per-surface native
description; a round trip establishes it before use. Unavailable/rejected metadata uses the prepared
default description. Fixed Windows-scRGB does not support these metadata changes.

Source intent and metadata survive recreation/reconnect and are cleared by false. They remain
separate from nominal compositor feedback. Relative intent derives current headroom from valid
preferred target peak/reference white and uses that ratio as the maximum fallback. Perceptual
intent leaves the ratios unknown. Neither case turns source metadata into display feedback.
These are surface-state hints, not a frame-indexed metadata API. See the
[HDR surface API](hdr-api-design.md) for source-field semantics and native limits.

## Windows

No documented public equivalent was found for Avalonia's Win32 scRGB composition surfaces with the
display-wide **Use HDR** setting disabled. The backend therefore does not expose this feature.
HDR video playback through the Windows media pipeline is a separate presentation path, not a
headroom hint that can be attached to an existing Avalonia surface. This does not establish whether
the first-party player's implementation uses private APIs.

The relevant public APIs do not provide the required behavior:

- [DirectX Advanced Color](https://learn.microsoft.com/en-us/windows/win32/direct3darticles/high-dynamic-range)
  allows FP16 scRGB surfaces on SDR outputs, but clips values outside the SDR range. Surface color
  space selection alone does not enable HDR on the output.
- [IDXGISwapChain4.SetHDRMetaData](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_5/nf-dxgi1_5-idxgiswapchain4-sethdrmetadata)
  describes content; it is not an HDR-mode request. Microsoft discourages its use and does not
  guarantee the metadata reaches the monitor.
- `BrightnessOverride` and `DisplayEnhancementOverride` are
  [unsupported in desktop apps](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-supported-api).
  They also describe brightness or color-processing overrides, not scRGB highlight headroom.
- The `DisplayConfigSetDeviceInfo` advanced-color/HDR state requests configure a display target,
  not a window. Toggling them would change display-wide HDR, contrary to this feature's purpose.

## Android device verification

Use an HDR-capable API 35+ device with extended-linear EGL active. Start with SDR content and the
default false hint, show HDR content with the hint true, then remove it and reset the hint. Observe
the reported color volume and highlight brightness through both transitions. Change content
headroom while true remains true, then clear metadata with `SetHdrContent(true, null)` to return to
automatic selection. Repeat after native
surface recreation, rotation, and background/resume; check that other visible HDR surfaces can
still keep the display's headroom active. Also check that standard/software fallbacks return no
hint feature. Screenshots alone cannot verify emitted HDR luminance.