using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using CoreFoundation;
using Foundation;
using ObjCRuntime;

namespace Avalonia.iOS;

internal unsafe class Interop
{
    internal const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    internal const string libcLibrary = "/usr/lib/libc.dylib";
    // CoreFoundation matches the common modes pseudo-mode by pointer identity, so the real constant is required.
    internal static readonly IntPtr kCFRunLoopCommonModes =
        Dlfcn.GetIntPtr(Dlfcn.dlopen(CoreFoundationLibrary, 0), "kCFRunLoopCommonModes");
    internal const uint QOS_CLASS_USER_INTERACTIVE = 0x21;

    [Flags]
    internal enum CFOptionFlags : ulong
    {
        kCFRunLoopBeforeSources = (1UL << 2),
        kCFRunLoopAfterWaiting = (1UL << 6),
        kCFRunLoopBeforeWaiting = (1UL << 5)
    }
    
    [DllImport(libcLibrary)]
    internal static extern void dispatch_async_f(IntPtr queue, IntPtr context, delegate* unmanaged<IntPtr, void> dispatch);

    [DllImport(libcLibrary)]
    internal static extern int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);

    [DllImport(CoreFoundationLibrary)]
    internal static extern IntPtr CFRunLoopGetMain();
    
    [DllImport(CoreFoundationLibrary)]
    internal static extern IntPtr CFRunLoopGetCurrent();

    [DllImport (CoreFoundationLibrary)]
    internal static extern void CFRunLoopWakeUp(IntPtr rl);
    
    [DllImport(CoreFoundationLibrary)]
    internal static extern IntPtr CFRunLoopObserverCreate(IntPtr allocator, CFOptionFlags activities,
        int repeats, int index, delegate* unmanaged<IntPtr, CFOptionFlags, IntPtr, void> callout, IntPtr context);

    [DllImport(CoreFoundationLibrary)]
    internal static extern IntPtr CFRunLoopAddObserver(IntPtr loop, IntPtr observer, IntPtr mode);

    [DllImport(CoreFoundationLibrary)]
    internal static extern IntPtr CFRunLoopTimerCreate(IntPtr allocator, double firstDate, double interval,
        CFOptionFlags flags, int order, delegate* unmanaged<IntPtr, IntPtr, void> callout, IntPtr context);

    [DllImport(CoreFoundationLibrary)]
    internal static extern void CFRunLoopTimerSetTolerance(IntPtr timer, double tolerance);

    [DllImport(CoreFoundationLibrary)]
    internal static extern void CFRunLoopTimerSetNextFireDate(IntPtr timer, double fireDate);

    [DllImport(CoreFoundationLibrary)]
    internal static extern void CFRunLoopAddTimer(IntPtr loop, IntPtr timer, IntPtr mode);

    [DllImport(CoreFoundationLibrary)]
    internal static extern double CFAbsoluteTimeGetCurrent();
}
