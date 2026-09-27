using System;
using System.Diagnostics;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Wayland.Server.Persistent;

namespace Avalonia.Wayland;

partial class WindowBaseImpl
{
    partial class Sink
    {
        private WaylandScrollInertia? _scrollInertia;

        void IWSurfaceEventSink.OnPointerEnter(ulong timestamp, uint serial, Point position)
        {
            _scrollInertia = null;
            if (InputRoot is null)
                return;
            ScheduleInput(new RawPointerEventArgs(Mouse, timestamp, InputRoot,
                RawPointerEventType.Move, position, RawInputModifiers.None));
        }

        void IWSurfaceEventSink.OnPointerLeave(uint serial)
        {
            _scrollInertia = null;
            if (InputRoot is null)
                return;
            ScheduleInput(new RawPointerEventArgs(Mouse, 0, InputRoot,
                RawPointerEventType.LeaveWindow, new Point(), RawInputModifiers.None));
        }

        void IWSurfaceEventSink.OnPointerMotion(ulong timestamp, Point position, RawInputModifiers modifiers)
        {
            if (_scrollInertia?.IsActive == true)
                _scrollInertia = null;
            if (InputRoot is null)
                return;
            ScheduleInput(new RawPointerEventArgs(Mouse, timestamp, InputRoot,
                RawPointerEventType.Move, position, modifiers));
        }

        void IWSurfaceEventSink.OnPointerButton(ulong timestamp, uint serial, RawPointerEventType type,
            RawInputModifiers modifiers, Point position, object? platformCookie)
        {
            _scrollInertia = null;
            if (InputRoot is null)
                return;
            ScheduleInput(new RawPointerEventArgs(Mouse, timestamp, InputRoot,
                type, position, modifiers) { PlatformInputEventCookie = platformCookie });
        }

        void IWSurfaceEventSink.OnPointerAxis(ulong timestamp, Vector delta, RawInputModifiers modifiers,
            Point position, bool isTouchpad, TouchpadGesturePhase gesturePhase)
        {
            if (InputRoot is null)
                return;

            if (!isTouchpad || gesturePhase is TouchpadGesturePhase.None or TouchpadGesturePhase.Cancelled)
                _scrollInertia = null;
            else if (gesturePhase == TouchpadGesturePhase.Began)
            {
                _scrollInertia = new WaylandScrollInertia();
                _scrollInertia.Start(timestamp);
            }

            _scrollInertia?.AddDelta(timestamp, delta);
            ScheduleInput(new RawMouseWheelEventArgs(Mouse, timestamp, InputRoot,
                position, delta, modifiers) { IsTouchpad = isTouchpad, GesturePhase = gesturePhase });

            if (gesturePhase == TouchpadGesturePhase.Ended)
                StartScrollInertia(timestamp, modifiers, position);
        }

        private void StartScrollInertia(ulong timestamp, RawInputModifiers modifiers, Point position)
        {
            var inertia = _scrollInertia;
            if (inertia?.End(timestamp) != true)
            {
                _scrollInertia = null;
                return;
            }

            var clock = Stopwatch.StartNew();
            MediaContext.Instance.RequestAnimationFrame(OnAnimationFrame);

            void OnAnimationFrame(TimeSpan frameTime)
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (_scrollInertia != inertia || IsDisposed || InputRoot is null || !Parent.IsEnabled)
                        return;

                    var elapsed = clock.Elapsed;
                    var args = new RawMouseWheelEventArgs(Mouse, timestamp + (ulong)elapsed.TotalMilliseconds,
                        InputRoot, position, inertia.GetDelta(elapsed), modifiers)
                    {
                        IsTouchpad = true,
                        GesturePhase = TouchpadGesturePhase.Inertia
                    };
                    DispatchInput(args);

                    if (_scrollInertia != inertia)
                        return;

                    if (args.Handled && inertia.IsActive)
                        MediaContext.Instance.RequestAnimationFrame(OnAnimationFrame);
                    else
                        _scrollInertia = null;
                }, DispatcherPriority.Input);
            }
        }

        void IWSurfaceEventSink.OnPointerGesture(ulong timestamp, RawPointerEventType type, Vector delta,
            RawInputModifiers modifiers, Point position)
        {
            _scrollInertia = null;
            if (InputRoot is null)
                return;
            ScheduleInput(new RawPointerGestureEventArgs(Mouse, timestamp, InputRoot,
                type, position, delta, modifiers));
        }

        void IWSurfaceEventSink.OnTouchDown(ulong timestamp, int touchId, Point position, object? platformCookie)
        {
            _scrollInertia = null;
            if (InputRoot is null)
                return;
            ScheduleInput(new RawTouchEventArgs(Touch, timestamp, InputRoot,
                    RawPointerEventType.TouchBegin, position, RawInputModifiers.None, touchId)
                { PlatformInputEventCookie = platformCookie });
        }

        void IWSurfaceEventSink.OnTouchMove(ulong timestamp, int touchId, Point position)
        {
            if (InputRoot is null)
                return;
            ScheduleInput(new RawTouchEventArgs(Touch, timestamp, InputRoot,
                RawPointerEventType.TouchUpdate, position, RawInputModifiers.None, touchId));
        }

        void IWSurfaceEventSink.OnTouchUp(ulong timestamp, int touchId, Point position)
        {
            if (InputRoot is null)
                return;
            ScheduleInput(new RawTouchEventArgs(Touch, timestamp, InputRoot,
                RawPointerEventType.TouchEnd, position, RawInputModifiers.None, touchId));
        }

        void IWSurfaceEventSink.OnTouchCancel(int touchId, Point position)
        {
            if (InputRoot is null)
                return;
            ScheduleInput(new RawTouchEventArgs(Touch, 0, InputRoot,
                RawPointerEventType.TouchCancel, position, RawInputModifiers.None, touchId));
        }
    }
}
