using System;
using System.Runtime.InteropServices;

namespace Ghuboon.App.Platform.MacOS;

/// <summary>
/// Minimal raw P/Invoke surface into the Objective-C runtime and AppKit
/// needed for <see cref="MacOSMenuBarHost"/>. We deliberately avoid pulling
/// in MonoMac / Xamarin.Mac to keep dependencies at zero (per Phase 12 lane
/// instructions); this class hides the messy details so the host code can
/// read closer to Objective-C.
///
/// Notes:
/// - Selectors and class pointers are resolved lazily by name.
/// - We bind only the few <c>objc_msgSend</c> overloads we actually need.
///   Each native signature must be its own managed entry point because
///   <c>objc_msgSend</c> is variadic in C; passing the wrong shape will
///   silently corrupt the stack.
/// </summary>
internal static class AppKitInterop
{
    private const string ObjC = "/usr/lib/libobjc.dylib";

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    public static extern IntPtr GetClass(string name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    public static extern IntPtr SelRegisterName(string name);

    [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")]
    public static extern IntPtr AllocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);

    [DllImport(ObjC, EntryPoint = "objc_registerClassPair")]
    public static extern void RegisterClassPair(IntPtr cls);

    [DllImport(ObjC, EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool ClassAddMethod(IntPtr cls, IntPtr selector, IntPtr imp, string types);

    // ---- objc_msgSend overloads -------------------------------------------------
    // Each native shape needs its own managed entry point.

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr_String(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg1);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern IntPtr SendIntPtr_String_IntPtr_String(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string title, IntPtr action, [MarshalAs(UnmanagedType.LPUTF8Str)] string keyEquivalent);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    // Used by the cleanup path to balance +1 retain counts from alloc/init.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    public static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(ObjC, EntryPoint = "objc_disposeClassPair")]
    public static extern void DisposeClassPair(IntPtr cls);

    // ---- Foundation: NSThread / dispatch_get_main_queue ------------------------
    // Used by UpdateUnreadCount to detect off-main calls and marshal to the main
    // dispatch queue (Issue #17). Returning a Boolean from Objective-C requires
    // calling the dedicated bool-returning overload; libobjc's BOOL is signed
    // char, so we marshal as I1.

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool SendBool(IntPtr receiver, IntPtr selector);

    // dispatch_get_main_queue() lives in libdispatch.dylib on macOS but is
    // re-exported by libSystem (always loaded). Importing from libSystem keeps
    // us free of an extra DllImport target across runner architectures.
    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dispatch_get_main_queue")]
    public static extern IntPtr DispatchGetMainQueue();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DispatchBlock();

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dispatch_async_f")]
    public static extern void DispatchAsyncF(IntPtr queue, IntPtr context, IntPtr work);
}
