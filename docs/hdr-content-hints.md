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

## macOS

Opt in with `AvaloniaNativePlatformOptions.ColorMode = AvaloniaNativeColorMode.ExtendedLinear`.
Metal on macOS 10.15 or newer uses FP16 extended-linear sRGB for both SDR wide gamut and HDR.
The default false hint does not request extended brightness; true sets
`CAMetalLayer.wantsExtendedDynamicRangeContent`. Clearing it leaves the same FP16 surface and
wide-gamut color space in place. Standard, OpenGL, software and older-system fallbacks remain
color-managed sRGB; accepting a hint on those paths cannot activate HDR.

The hint is retained and reapplied when a Metal render target is recreated. Source metadata is
normalized and retained, including metadata-only changes, but is not submitted as `CAEDRMetadata`.
This is intentionally a client-mapped, relative-white path: white is 1, and the client fits highlights
to current headroom. Installing a native tone-mapping curve as well would change that contract.
Source white/mastering nits are never substituted for display measurements.

With the hint true, current headroom comes from the window screen's
`maximumExtendedDynamicRangeColorComponentValue`. False reports a one-times surface target.
Potential headroom is independently reported by
`maximumPotentialExtendedDynamicRangeColorComponentValue` and is not a grant. Missing/invalid
ratios remain unknown, with the standard current-ratio fallback for missing potential headroom.
No absolute luminance is derived from these ratios.

AppKit screen/profile, window migration/visibility, backing-property and attachment notifications
refresh the snapshot. Events run on the UI thread; render sessions use a native cached snapshot
without calling AppKit from the render thread. Native resource allocation may update current
headroom asynchronously after a hint. Brightness, reference modes and other visible HDR content
can affect it. No global display setting is changed.

## iOS and iPadOS

Opt in with `iOSPlatformOptions.ColorMode = iOSColorMode.ExtendedLinear`. Metal on iOS/iPadOS
and Mac Catalyst 16 or newer uses FP16 extended-linear sRGB. The default false hint keeps wide
gamut available without requesting EDR. True sets `CAMetalLayer.wantsExtendedDynamicRangeContent`;
false clears it without changing the format or white scale. Older systems and tvOS Metal remain
color-managed SDR. OpenGL retains its SDR path without these optional features.

The client-mapped relative-white contract and normalization are shared with macOS: white is 1,
`CAEDRMetadata` remains unset, and source metadata is retained rather than used as display data.
Metadata-only changes are accepted, false clears metadata, and render-target recreation does not
reset the surface's intent or source metadata. A hint on a standard Metal surface cannot enable HDR.

The attached window's screen supplies `UIScreen.currentEDRHeadroom` when the hint is true and
`potentialEDRHeadroom` independently. False reports current headroom 1 while attached. Detached
extended surfaces have unknown current and potential headroom; no global main-screen reading is
substituted. Native feedback is normalized without inventing nits or replacing current headroom
with potential headroom.

A main-thread display link polls native headroom while the view is attached and the application
is active, so asynchronous changes repaint even otherwise static content. Attachment, layout,
traits and activation also refresh the report. Inactivity stops polling; detachment and disposal
release the link and application observers. Change events run on the UI thread and repainting is
deferred to avoid compositor re-entry. Rendering sessions capture complete immutable snapshots
without reading UIKit on the render thread. No display-wide brightness or HDR setting is changed.

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

## macOS device verification

Run the [HDR probe](hdr-api-design.md#demo-and-validation) with `extendedlinear --self-test` on an
EDR-capable Mac. Check that SDR/WCG uses the FP16 surface at white 1 before requesting HDR,
that native current headroom updates after the hint, and that clearing it retains wide gamut
while returning to a one-times target. Repeat with the default standard mode and the OpenGL
and software renderer overrides; those paths must remain color-managed SDR.

Move the window between HDR/WCG/SDR displays, change system brightness and reference modes,
and hide/show or recreate the surface. Potential headroom must never replace current headroom;
source metadata must not change reported nits or mapping ownership. Validate emitted highlights
on the display itself; SDR screenshots and FP16 readbacks do not prove physical HDR luminance.

## iOS device verification

On an EDR-capable iPhone or iPad running iOS/iPadOS 16 or newer, opt in to extended-linear Metal.
Check that the actual session reports FP16 linear sRGB at white 1 before setting a hint, and that
SDR wide-gamut content retains negative and above-one components. Set the hint before waiting for
headroom, observe native current/potential changes, update source metadata while true remains
true, then clear the hint. Clearing must retain FP16 and wide gamut while reporting current
headroom 1; no source nits may appear as display measurements.

Repeat through render-target recreation, native view detach/reattach, rotation, external-display
migration and application background/resume. A detached extended surface must not report the main
screen's headroom, and an in-flight rendering session must retain its original snapshot. Verify
that polling and observers are released on detach/disposal and resume on reattachment/activation.
Check default standard Metal, older-system and tvOS fallbacks separately. Validate emitted HDR
highlights on physical hardware; a simulator, SDR screenshot or FP16 readback alone is insufficient.