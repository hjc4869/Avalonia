# HDR content hints

`IPlatformHdrContentFeature` separates an HDR-capable rendering surface from a request for HDR
headroom. The surface can remain extended-linear while displaying SDR content, without requesting
extra display brightness just because the application started.

Call the optional feature on the UI thread when visible content needs luminance above reference
white. Reset it when that content is removed or rendered as SDR. The default is false. Applications
with multiple HDR views must combine their requests for the same top level.

```csharp
using Avalonia.Platform;

topLevel.PlatformImpl?.TryGetFeature<IPlatformHdrContentFeature>()?.SetHdrContent(hasHdrContent);
```

This is an application-supplied hint, not automatic pixel analysis. Do not wait for the currently
reported headroom to exceed 1 before setting it: the platform may need the hint to make that
headroom available. Continue using `IPlatformSurfaceColorVolumeFeature` and its change event to
adapt rendering to the headroom actually granted. Other visible HDR surfaces and system policies
can affect the result.

## Android

The feature is available when `AndroidColorMode.ExtendedLinear` successfully negotiates FP16 scRGB
through EGL on Android 15 (API 35) or newer. Standard, software, Vulkan, and unsupported-device
fallbacks do not expose it.

The backend uses the public
[SurfaceView.SetDesiredHdrHeadroom](https://learn.microsoft.com/en-us/dotnet/api/android.views.surfaceview.setdesiredhdrheadroom)
API on Avalonia's own surface:

- False requests `1.0`, which means no HDR headroom. `0.0` would instead restore Android's automatic
  selection and could engage HDR without any HDR content.
- True restores the existing request derived from the display's maximum luminance and scRGB's
  80-nit reference white, capped at scRGB's 10,000-nit maximum. If those capabilities are unavailable,
  Android's automatic selection is used.

The hint is retained across native surface recreation and reapplied on surface creation and display
or configuration changes. It does not recreate EGL buffers, change their color space, set activity
window color mode, or override the user's brightness setting. The surface API is independent of
`Window.SetDesiredHdrHeadroom`, which does not control `SurfaceView` layers. Actual headroom remains
subject to Android's display, ambient-light, and power policies.

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
the reported color volume and highlight brightness through both transitions. Repeat after native
surface recreation, rotation, and background/resume; check that other visible HDR surfaces can
still keep the display's headroom active. Also check that standard/software fallbacks return no
hint feature. Screenshots alone cannot verify emitted HDR luminance.