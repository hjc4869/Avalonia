# Wayland platform


## Special considerations

Unlike other platforms where the OS provided window server is considered to be stable, with Wayland it's EXPECTED for
compositor to crash and restart during normal usage. This means that applications need to be able to handle this situation
gracefully. So Avalonia.Wayland platform maintains its own state of the wayland surfaces in a way that can be re-uploaded
to the compositor on restart. It also means that all kinds of resources are considered to be transient and are a subject
to be re-created as needed.

Another difference with our other platform implementations, is that Wayland can't have the same render timer concept:
the compositor is controlling when the application should render next frame for EACH toplevel by sending frame callbacks.
This means that we need to react fast to said callbacks to provide acceptable frame rate. So our wayland event loop
runs on a dedicated thread that also serves as our render thread. Yes, Wayland worker thread == Avalonia's render thread.



NOTE: crash recovery is not yet supported when using an externally created wl_display.

## Color management (wide color gamut / HDR)

Off by default. `WaylandPlatformOptions.ColorMode` opts in:

- `WideColorGamut` — 10 bit (fp16 fallback) surface, Display P3 primaries with a gamma 2.2 transfer
  function. Existing controls keep their appearance because Skia color converts their sRGB colors
  into the wider space; blending stays gamma-encoded so gradients and antialiasing are unaffected.
- `ExtendedLinear` — fp16 scRGB surface (sRGB primaries, extended linear transfer). Channel values
  below 0 and above 1 are meaningful, so it's the HDR-capable mode, but blending happens in linear
  light and therefore differs from Avalonia's historical sRGB-encoded blending. Falls back to
  BT.2020 PQ when the compositor supports PQ but cannot accept scRGB.

`HdrPresentationPreferences` overrides color selection with an ordered list of complete HDR
candidates: `LinearRelative`, `LinearPerceptual`, `WindowsScRgbRelative`,
`WindowsScRgbPerceptual`, `PqRelative`, `PqPerceptual`, and `Sdr`. Relative intent means client
highlight fitting; perceptual intent delegates adaptation to the compositor. Empty means SDR;
null uses `ColorMode`. The default HDR order is the order above. `Sdr` terminates negotiation.
Only the WSI rendering path supports HDR; dmabuf and software paths remain SDR.

Both sides have to agree, and negotiation runs in `WaylandGlobals` on connection setup/reconnect:

1. Bind `wp_color_manager_v1` and read the compositor's supported features / transfer functions /
  primaries and render intents.
2. Only then create the EGL display, passing high bit depth `EglColorBufferFormat` candidates with
   the plain 8 bit config last as a fallback.
3. Create the image descriptions for whatever EGL actually handed us, and tag each `wl_surface`
    with its content range via `wp_color_management_surface_v1.set_image_description`.
  4. For HDR, skip unsupported candidates and retry the next preference after an EGL or description
    failure. Log the actual selected candidate; SDR is the final fallback.

**Invariant: what we render and what we tag the surface with must always match.** An untagged
surface is interpreted as sRGB, so rendering P3 pixels into one shifts every color on screen. If
the compositor refuses the image description, `WaylandEglWsiPlatformGraphics.DowngradeToUnmanagedColorSpace`
puts Skia back to plain sRGB; the buffer keeps whatever extra precision EGL gave us, which is
always safe. HDR negotiation failures advance through the list and ultimately select SDR.

Known limitation: the software `WaylandFramebuffer` fallback always renders 8 bit sRGB, so if a
tagged surface ever falls back to it, colors will be off until the surface is re-tagged.

The extended linear description also sets its luminances explicitly, because the protocol's default
primary color volume minimum is 0.2 cd/m² and linear light has no such floor: 0 is no emission at
all. Left at the default, the compositor has a black level to map out of the surface and it arrives
as lifted shadows, which is the one thing an extended range surface is supposed to reproduce exactly.
Only the minimum is changed; the maximum and reference white stay at scRGB's 80 cd/m², so signal 1.0
still means the reference white.

The parametric extended-linear descriptions declare a separate BT.2020 target volume with an
80-nit peak for SDR content and a 10,000-nit peak for HDR content without source metadata. Both descriptions preserve the
surface's sRGB primaries, linear encoding, and reference white. This requires the compositor's
`set_mastering_display_primaries` and `extended_target_volume` features; otherwise the backend uses
the Windows-scRGB fallback when available. If neither scRGB description is supported, it tries
BT.2020 primaries with the PQ transfer function, using a 10-bit buffer with fp16 as a fallback.
PQ uses the protocol's 203-nit reference white and 10,000-nit encoding maximum. Skia maps ordinary
SDR white to the reference white, preserving the remaining range for HDR highlights.

Skia composes PQ windows in the retained FP16 scRGB layer. Video, text, transparency, and effects
operate in linear light, preserving negative and above-white components until the final draw
converts the layer to BT.2020 PQ. The final draw requests dithering for 10-bit destinations.

Custom drawing uses `ISkiaSharpApiLease.ColorFormat`, which reports `RgbaF16/ScRgbLinear` for this
composition layer. Its `PreferredColorVolume.ReferenceWhiteScale` is 1, while
`SurfaceNitsPerUnit` is null: white is relative, not measured in nits. The native surface is PQ-encoded, with
HDR represented within `[0, 1]`.

### HDR content hint

`IPlatformHdrContentFeature` is an optional, platform-neutral top-level feature. Applications call
`SetHdrContent(true, metadata)` on the UI thread while visible content needs luminance above reference white,
and `SetHdrContent(false, null)` when that source content is removed or deliberately switched to SDR.
Set true before waiting for current headroom; temporarily fitting content to SDR must not clear
the request. The default is false;
applications with multiple HDR views must combine their requests for the same top level.

```csharp
topLevel.PlatformImpl?.TryGetFeature<IPlatformHdrContentFeature>()?.SetHdrContent(
  hasHdrContent, hasHdrContent ? new PlatformHdrContentMetadata(HeadroomRatio: 8) : null);
```

Without source metadata, Wayland switches prepared linear SDR/HDR descriptions without recreating
buffers; PQ uses its default description. With supported source metadata, parametric linear/PQ
creates a per-surface description carrying mastering luminance and content light level, waits for
its creation round trip, then sets it with the selected intent. Source nits are rescaled from
`ReferenceWhiteNits` to the native 80-nit linear or 203-nit PQ reference. A content headroom ratio
can be used without absolute source nits. No source value becomes a display measurement.

Metadata-only updates are applied while HDR remains true. Replaced descriptions are released;
intent and metadata are retained across native recreation/reconnect, and false clears metadata.
Unsupported/unrepresentable or rejected metadata uses the prepared default description. The fixed
Windows-scRGB description cannot carry these updates. The compositor still decides available
headroom, independently of the request. These surface-state hints are not frame-indexed metadata.
See [HDR content hints](../../docs/hdr-content-hints.md) for the Android implementation and Windows
API limitations, and the [HDR surface API](../../docs/hdr-api-design.md) for metadata conversion limits.

### Peak luminance / reference white

Each successfully tagged `WSurface` owns a `wp_color_management_surface_feedback_v1`
(`WaylandColorVolumeFeedback`). It reports the compositor's preferred image description, which
can change when the surface moves between outputs, announced through `preferred_changed`.

The chain is `get_preferred` → `wp_image_description_v1.ready` → `get_information` →
`wp_image_description_info_v1.done`. Only `done` publishes; `luminances` provides the primary range
plus the reference white, and `target_luminance` a target range. Both minimums are scaled by 10000
in the protocol, the other values are plain cd/m². The
result surfaces as `PlatformSurfaceColorVolume` through the `IPlatformSurfaceColorVolumeFeature`
top level feature and, snapshotted per frame, on `ISkiaSharpApiLease.PreferredColorVolume`.

The protocol explicitly permits theoretical target luminance. `LuminanceBasis` is `Nominal` for
received white/target values, not `DisplayReported`. With relative intent, finite positive target
peak and reference white from the same feedback snapshot yield
`HeadroomRatio = max(1, target peak / reference white)`, provided the ratio is finite.
`MaximumHeadroomRatio` falls back to the same ratio. Missing/invalid inputs leave both unknown;
perceptual intent also leaves both unknown. This derived client-rendering target is not a
calibrated physical headroom measurement, and the maximum fallback is not an independent
potential-headroom report. Primary encoding range and minimum luminance are not required.

The same round trip carries `tf_named`/`tf_power`, reported as `PlatformSurfaceColorVolume.Transfer`.
This is preferred-encoding metadata, not the source content's transfer function. The protocol
leaves untagged surfaces implementation-defined. Named power laws are reported as `Power` with
an exponent; source material must still be decoded according to its own encoding.

Partial answers publish only the valid fields received. Pending, failed and ICC-only feedback
does not erase the independently known surface scale or mapping policy. Without a successfully
tagged surface there is no HDR report.

`WaylandColorManager` adds the actual presentation contract. Parametric linear/PQ composition
reports `ReferenceWhiteScale=1` with no absolute nits/unit. Windows-scRGB reports fixed
`SurfaceNitsPerUnit=80`, but leaves diffuse-white scale unknown because feedback does not establish
a physical white mapping. The selected intent determines `ToneMapping`, independently of nits.

Native PQ encodes up to 10,000 nominal nits. The final conversion maps the drawing layer's relative
white to the PQ description's 203-nit reference, without treating that constant as display white.
See the [HDR surface API](../../docs/hdr-api-design.md) for field definitions and the mode test matrix.

### NWayland pitfalls hit here

- Passing an `IWlTargetQueue` **without** a listener throws. Interfaces with no events
  (`wp_image_description_creator_params_v1`, `wp_color_management_surface_v1`) still need an empty
  listener subclass.
- Passing an explicit target queue to a **destructor request** (`wp_image_description_creator_params_v1.create`)
  makes NWayland route the call through a proxy wrapper and then destroy the wrapper, which aborts
  inside libwayland with `Tried to destroy wrapper with wl_proxy_destroy()`. Pass a `null` queue for
  those and let the new object inherit its parent's queue.
- `create` is a destructor: the creator must not be disposed or destroyed afterwards.

### Choosing an extended linear description

`create_windows_scrgb` pins signal 1.0 to 80 cd/m², not to the reference white, which makes ordinary
SDR content visibly dimmer (measured: white composited at 167/255 instead of 255/255). The
parametric description with the default sRGB luminances puts the reference white at 1.0, matching
`EGL_EXT_gl_colorspace_scrgb_linear`, so it is preferred and `create_windows_scrgb` is only a
fallback.

## Touchpad input

Finger scrolling sets `PointerWheelEventArgs.IsTouchpad` while retaining Avalonia's wheel-delta units.
One wheel unit corresponds to 50 DIPs in `ScrollContentPresenter`; controls can use the flag to
distinguish touchpad panning from mouse-wheel actions.

`PointerWheelEventArgs.GesturePhase` identifies the beginning and updates of finger scrolling.
The final active axis's `wl_pointer.axis_stop` emits an `Ended` event with zero delta, so controls
can settle immediately on release. Stopping only one axis keeps the sequence active. Leaving the
surface or starting a pinch cancels an active scroll sequence. Mouse wheel events use `None`.

After finger release, Wayland generates frame-paced `Inertia` wheel events using the touchscreen
scroll recognizer's velocity estimator, velocity limits, resistance, and stopping speed. Estimation
uses DIPs (50 per wheel unit), preserving the existing wheel-delta scale. A release more than 200 ms
after the last movement does not start inertia. New scrolling, pointer movement, buttons, touch,
pinch, surface leave, or surface disposal cancels momentum; unhandled scrolling also stops it.
Mouse wheels and pinch translation do not generate inertia.

Native pinch input uses `zwp_pointer_gestures_v1` versions 1 through 2. Each pointer owns a pinch
listener, and gestures target the surface supplied by the begin event. The absolute protocol scale
is converted into incremental `PointerTouchPadGestureMagnify` deltas: multiplying the current scale
by `1 + Delta.Y` applies one update. Logical-center movement produces touchpad-marked wheel events,
and rotation produces `PointerTouchPadGestureRotate` deltas in clockwise degrees. Ending or
cancelling a pinch clears its target and scale state.

## Protocol docs

Do NOT assume things about Wayland protocols. Those could be rather non-intuitive. Always check what the protocol says
about a particular event.

LLMs should expect to find protocol docs in `<solution_dir>/../NWayland/external/{wayland|wayland-protocols|plasma-wayland-protocols|wlr-protocols}`
Humans using LLM agents are expected to clone NWayland (https://github.com/AvaloniaUI/NWayland) there.

NWayland generally comments protocol docs as C# XML comments in generated bindings, so those can be used as the source of information too.
LLMs should NEVER attempt to disassemble NWayland.dll or attempt to extract strings from it or use similar silly practices to extract protocol binding information, use .xml files from nuget cache instead.
NWayland converts snake_case to CamelCase, but XML docs might still be mentioning things by snake_case naming.


## Architecture 

We are using NWayland as wayland bindings. We are always using a dedicated wayland queue for all of our wayland interactions so
Avalonia can potentially be embedded into another toolkit (if said toolkit actually verifies that it gets events from objects it owns).

Wayland interactions are running on a dedicated thread that also serves as our render thread.
We are sending most of our UI->Wayland commands as a part of our composition batches, so they arrive alongside with
the information required to render frame, so the wayland thread  always works with a consistent view of the UI state.
There are also OOB commands for special cases that bypass regular Compositor's commit cycle.

## "Persistence"

To handle compositor restarts, we maintain a "persistent" state of our surfaces and resources (see Server/Persistent dir).
Entities bound to a connection defined in Server/Transient dir, they should be considered to be ephemeral.

## Render timer

Since wayland asks us nicely to NOT render when we want to, but instead tells us when to, the render timer is not an actual
timer, but something that gets triggered by frame callbacks. So if we expect the render timer to do something useful 
for e. g. new surface, we need to wake it up explicitly.

Ordinary composition wakeups are coalesced and handled on the Wayland worker. Mapped surfaces request a frame callback,
reusing an outstanding request when possible. A callback-only commit does not attach another buffer or force a redraw.
Render tasks that request another tick keep these callbacks active even when the current tick has no new pixels to submit.
Once no task needs another tick, the loop stops requesting callbacks.

With no mapped surface, composition work can run immediately. Native platform jobs can also wake the loop immediately.
Batches deferred for a frame callback arm the starvation timer, including out-of-band disposal batches. This lets an
occluded window make bounded progress even when the compositor withholds its frame callbacks. Frame requests use the
surface's out-of-band commit guard, so they do not commit an unconfigured xdg surface.
For inevitable oversights there is currently a "fallback" timer that ticks at 20FPS, but it should be removed once
we are sure that all the cases are covered (this will likely require some refactoring of UI thread's animation engine).

## Threading rules

**NO CROSS-THREAD VARIABLE ACCESS. MESSAGING ONLY.**

- Wayland interactions run on a dedicated thread (the wayland worker thread).
- UI thread objects must NEVER directly access fields of wayland-thread objects (aside from initial creation / passing to constructors).
- Wayland thread objects must NEVER directly read fields of UI-thread objects. No volatile fields, no locks, no "safe" shared state.
- All cross-thread communication from UI to wayland goes through code-generated proxies that internally route calls through `WaylandWorker.PostOob()` / `PostWithCommit()` messages (dnd/clipboard is currently an exception to this rule that we'll probably refactor later)
- ~~Input (mouse/touch/keyboard) events from wayland to UI flow through `AutomaticRawEventGrouperDispatchQueue` (enqueued on wayland thread, drained by UI thread dispatcher).~~ (this was the plan, but we need an input root to issue events from non-UI tread, so for now we simply make calls with default priority and let the auto-grouper to dispatch them, will think what to do about it later).
- Server objects (in `Server/`) should not have their internal state modified from UI thread code. The UI thread may only call `Post` and pass values into server object constructors.

## Wayland protocol rules

### Globals and versioning

- The compositor announces globals via `wl_registry.global` with the **maximum** version it supports.
- The client chooses the version it wants when calling `Bind` (should be ≤ compositor's version, ≤ bindings version).
- Use `Math.Min(compositorVersion, known-supported-version)` as the bind version; skip if below minimum required.
- Once a proxy is bound at a version, all objects created through it (factory methods or arriving as events) inherit that version.
- Events for versions higher than the bound version simply do not arrive (safe degradation).

### Globals can come and go

- Globals like `wl_seat` and `wl_output` can appear and disappear dynamically via `wl_registry.global` / `global_remove`.
- There can be **multiple** instances of the same global type simultaneously (e.g., multiple seats representing different input device groups).
- Track globals by their registry `name` (uint) so they can be properly cleaned up on `global_remove`.
- Do not assume singletons — design data structures to handle multiple instances.

### wl_pointer frame semantics

- `wl_pointer` uses frame-based event delivery: events (enter, leave, motion, button, axis) accumulate, then a `frame` event signals the end of a logical group.
- **All events within a frame must be dispatched in arrival order.** A frame can contain leave from surface A followed by enter on surface B — dispatching in an arbitrary hardcoded order will route events to the wrong surface.
- Multiple `wl_pointer.axis` events within the same frame should be combined (e.g., H+V scroll into a single vector), but the combined result must be dispatched at the correct position in the event sequence (not after all other events).
- Frame state (focused sink, position, modifiers, accumulated events) belongs to the **pointer**, not the seat. A seat manages device lifecycle; each `wl_pointer` has its own independent frame grouping.
- Button codes are Linux `input-event-codes.h` constants: `BTN_LEFT=0x110`, `BTN_RIGHT=0x111`, `BTN_MIDDLE=0x112`, `BTN_SIDE=0x113`, `BTN_EXTRA=0x114`.

### NWayland specifics

- Listeners are passed at bind/creation time (e.g., `WlSeat.Bind(..., listener)`). There is no `SetListener` method. This is needed because of known race conditions in libwayland-client.
- `WlFixed` supports explicit cast to double: `(double)surfaceX`.
- Enums like `WlSeat.CapabilityEnum` support `.HasFlag()` and `==` comparison. Do not access `.value__` (those are actually enums it's just reference API file got generated by MSFT tool that turned them into classes for some reason)
- Do NOT attempt to re-declare wayland protocol enums even if generated method accepts int/uint. This is due to XML protocol specs not actually providing machine-readable information about a particular enum being expected by the request/event. The enums themselves are still generated.
- ALWAYS specify the version range of the protocol that's supported by Avalonia. NWayland supporting a particular protocol version binding-wise does NOT mean that we are ready to support it. Even if protocol says "stable" it does't mean that there aren't new requirements or invariants for the protocol clients to support.
- Proxy version is available from Version property on all proxies. If some request is only available from version X, NWayland generates Is<Name>Available property (e. g. `public bool IsSetReactiveAvailable => Version >= 3;`) that can be used instead of `Version` checks.
