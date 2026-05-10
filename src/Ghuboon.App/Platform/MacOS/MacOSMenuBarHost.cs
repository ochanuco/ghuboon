// Phase 12 (ADR-015): macOS menu-bar residency.
//
// Implementation status:
//   - Builds an NSStatusItem with a title ("Ghuboon").
//   - Builds an NSMenu with: Show, Sync now, Unread:N, Settings..., Quit.
//   - Hooks each menu item to a managed callback via an Objective-C
//     subclass created at runtime with class_addMethod. Each IMP is a
//     reverse-P/Invoke thunk obtained from
//     Marshal.GetFunctionPointerForDelegate over a static delegate
//     instance, which we keep rooted to keep the thunk alive. The IMP
//     routes through a single static instance pointer back into this
//     host. (We use this delegate-based path rather than C# 9 function
//     pointers / [UnmanagedCallersOnly] because that requires
//     <AllowUnsafeBlocks> in the csproj, which is out of scope for this
//     lane.)
//   - UpdateUnreadCount mutates the unread item's title in place.
//   - Dispose removes the NSStatusItem from the system menu bar.
//
// Caveats / deferred:
//   - Icon (NSImage) is intentionally not loaded; we use a text title for
//     MVP. Adding a template image is a small follow-up.
//   - Only one MacOSMenuBarHost may be live at a time (a static field
//     stores the active instance for native callbacks). This matches our
//     single-process App lifetime and is asserted in Initialize.
//   - The dynamic Objective-C subclass is registered exactly once per
//     process. Re-registration is a no-op.
//   - Native callbacks fire on the AppKit main thread. We do not marshal
//     to Avalonia's UI thread here; the App layer wraps the delegates in
//     Dispatcher.UIThread.Post (see App.axaml.cs).

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Ghuboon.App.Platform.MacOS;

public sealed class MacOSMenuBarHost : IMenuBarHost
{
    // Sentinel width that asks AppKit for variable-length status items.
    private const double NSVariableStatusItemLength = -1.0;

    // Identifies which menu item invoked the callback. Used only as the
    // index into our static thunk table when registering selectors with the
    // dynamic Objective-C class.
    private enum MenuAction
    {
        Show,
        Hide,
        Sync,
        Settings,
        Quit,
    }

    // Single-active-instance discipline lets us route raw IMP callbacks back
    // into managed code without per-instance class registration.
    private static MacOSMenuBarHost? s_active;
    private static int s_classRegistered; // 0 or 1, used with Interlocked.

    private static IntPtr s_targetClass;
    private static IntPtr s_targetInstance;
    private static IntPtr s_selShow;
    private static IntPtr s_selHide;
    private static IntPtr s_selSync;
    private static IntPtr s_selSettings;
    private static IntPtr s_selQuit;

    private MenuBarContext? _context;
    private IntPtr _statusItem;
    private IntPtr _unreadMenuItem;
    private bool _disposed;

    public void Initialize(MenuBarContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.CompareExchange(ref s_active, this, null) is not null)
        {
            throw new InvalidOperationException(
                "Another MacOSMenuBarHost is already initialized. Dispose it before creating a new one.");
        }

        _context = context;

        try
        {
            EnsureTargetClassRegistered();

            // [[NSStatusBar systemStatusBar] statusItemWithLength:NSVariableStatusItemLength]
            // statusItemWithLength: takes a CGFloat (double on x86_64/arm64 macOS),
            // so it needs the dedicated double overload of objc_msgSend rather
            // than the generic IntPtr-arg one.
            var statusBarClass = AppKitInterop.GetClass("NSStatusBar");
            var systemStatusBar = AppKitInterop.SendIntPtr(statusBarClass, AppKitInterop.SelRegisterName("systemStatusBar"));
            _statusItem = StatusItemWithLength(systemStatusBar, NSVariableStatusItemLength);

            // [statusItem setTitle:@"Ghuboon"]
            var nsStringClass = AppKitInterop.GetClass("NSString");
            var titleString = AppKitInterop.SendIntPtr_String(
                nsStringClass,
                AppKitInterop.SelRegisterName("stringWithUTF8String:"),
                "Ghuboon");
            AppKitInterop.SendVoid_IntPtr(_statusItem, AppKitInterop.SelRegisterName("setTitle:"), titleString);

            // Build the NSMenu.
            var menu = AllocInit("NSMenu");

            // Issue #17: balance the +1 retain count from AllocInit. setMenu: copies
            // the receiver, so once we hand it over we can release ours. We use
            // [menu autorelease] (rather than release immediately) to keep things
            // safe even if AppKit defers the copy under the hood.
            AddMenuItem(menu, "Show Ghuboon", s_selShow);
            AddMenuItem(menu, "Sync now", s_selSync);
            _unreadMenuItem = AddMenuItem(menu, FormatUnread(0), IntPtr.Zero); // disabled / informational
            AddSeparator(menu);
            AddMenuItem(menu, "Settings…", s_selSettings);
            AddSeparator(menu);
            AddMenuItem(menu, "Hide window", s_selHide);
            AddMenuItem(menu, "Quit Ghuboon", s_selQuit);

            AppKitInterop.SendVoid_IntPtr(_statusItem, AppKitInterop.SelRegisterName("setMenu:"), menu);
            // Balance ownership: AllocInit returned +1, NSStatusItem now retains
            // the menu via setMenu:, so we autorelease our reference.
            AppKitInterop.SendVoid(menu, AppKitInterop.SelRegisterName("autorelease"));
        }
        catch
        {
            // Issue #17 rollback discipline: undo any partial state we set so a
            // future Initialize attempt (or test run) starts clean.
            try
            {
                if (_statusItem != IntPtr.Zero)
                {
                    var statusBarClass = AppKitInterop.GetClass("NSStatusBar");
                    var systemStatusBar = AppKitInterop.SendIntPtr(statusBarClass, AppKitInterop.SelRegisterName("systemStatusBar"));
                    AppKitInterop.SendVoid_IntPtr(systemStatusBar, AppKitInterop.SelRegisterName("removeStatusItem:"), _statusItem);
                }
            }
            catch
            {
                // Best-effort cleanup; do not mask the original failure.
            }
            _statusItem = IntPtr.Zero;
            _unreadMenuItem = IntPtr.Zero;
            _context = null;
            // Reset the active-instance sentinel so a retry can succeed.
            Interlocked.CompareExchange(ref s_active, null, this);
            throw;
        }
    }

    public void UpdateUnreadCount(int count)
    {
        if (_disposed || _unreadMenuItem == IntPtr.Zero)
        {
            return;
        }

        // Issue #17: AppKit objects are main-thread-only. If we're called from a
        // background thread (e.g., a sync continuation), marshal the call to the
        // main dispatch queue. We pin a small managed payload via GCHandle so
        // dispatch_async_f can invoke a static trampoline that reads it back.
        if (IsMainThread())
        {
            ApplyUnreadCountOnMainThread(count);
            return;
        }

        var payload = new UnreadCountUpdate(this, count);
        var handle = GCHandle.Alloc(payload);
        try
        {
            AppKitInterop.DispatchAsyncF(
                AppKitInterop.DispatchGetMainQueue(),
                GCHandle.ToIntPtr(handle),
                s_dispatchUnreadTrampolinePtr);
        }
        catch
        {
            handle.Free();
            throw;
        }
    }

    private void ApplyUnreadCountOnMainThread(int count)
    {
        if (_disposed || _unreadMenuItem == IntPtr.Zero)
        {
            return;
        }

        var nsStringClass = AppKitInterop.GetClass("NSString");
        var newTitle = AppKitInterop.SendIntPtr_String(
            nsStringClass,
            AppKitInterop.SelRegisterName("stringWithUTF8String:"),
            FormatUnread(count));
        AppKitInterop.SendVoid_IntPtr(_unreadMenuItem, AppKitInterop.SelRegisterName("setTitle:"), newTitle);
    }

    private static bool IsMainThread()
    {
        try
        {
            var nsThread = AppKitInterop.GetClass("NSThread");
            return AppKitInterop.SendBool(nsThread, AppKitInterop.SelRegisterName("isMainThread"));
        }
        catch
        {
            // If we can't even talk to NSThread, assume we're not on main and
            // hop to the main queue defensively. The dispatch will be a no-op
            // on a non-AppKit host.
            return false;
        }
    }

    private sealed record UnreadCountUpdate(MacOSMenuBarHost Host, int Count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DispatchTrampoline(IntPtr context);

    private static readonly DispatchTrampoline s_dispatchUnreadTrampoline = DispatchUnreadCount;
    private static readonly IntPtr s_dispatchUnreadTrampolinePtr =
        Marshal.GetFunctionPointerForDelegate(s_dispatchUnreadTrampoline);

    private static void DispatchUnreadCount(IntPtr context)
    {
        if (context == IntPtr.Zero)
        {
            return;
        }
        var handle = GCHandle.FromIntPtr(context);
        try
        {
            if (handle.Target is UnreadCountUpdate u)
            {
                try { u.Host.ApplyUnreadCountOnMainThread(u.Count); }
                catch { /* swallow into native frame */ }
            }
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        if (_statusItem != IntPtr.Zero)
        {
            // [[NSStatusBar systemStatusBar] removeStatusItem:_statusItem]
            var statusBarClass = AppKitInterop.GetClass("NSStatusBar");
            var systemStatusBar = AppKitInterop.SendIntPtr(statusBarClass, AppKitInterop.SelRegisterName("systemStatusBar"));
            AppKitInterop.SendVoid_IntPtr(systemStatusBar, AppKitInterop.SelRegisterName("removeStatusItem:"), _statusItem);
            _statusItem = IntPtr.Zero;
        }

        _unreadMenuItem = IntPtr.Zero;
        _context = null;
        Interlocked.CompareExchange(ref s_active, null, this);
    }

    // ---- helpers ----------------------------------------------------------

    private static string FormatUnread(int count) => $"Unread: {count}";

    private static IntPtr AllocInit(string className)
    {
        var cls = AppKitInterop.GetClass(className);
        var allocated = AppKitInterop.SendIntPtr(cls, AppKitInterop.SelRegisterName("alloc"));
        return AppKitInterop.SendIntPtr(allocated, AppKitInterop.SelRegisterName("init"));
    }

    private static IntPtr StatusItemWithLength(IntPtr statusBar, double length)
    {
        // statusItemWithLength: takes a CGFloat (double on x86_64/arm64). We
        // need a dedicated overload so the .NET marshaller loads the value
        // into the FP register slot per the System V AMD64 / AArch64 ABI.
        var sel = AppKitInterop.SelRegisterName("statusItemWithLength:");
        return ObjcMsgSend_IntPtr_Double(statusBar, sel, length);
    }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr ObjcMsgSend_IntPtr_Double(IntPtr receiver, IntPtr selector, double arg);

    private IntPtr AddMenuItem(IntPtr menu, string title, IntPtr action)
    {
        // [[NSMenuItem alloc] initWithTitle:title action:action keyEquivalent:@""]
        var itemClass = AppKitInterop.GetClass("NSMenuItem");
        var allocated = AppKitInterop.SendIntPtr(itemClass, AppKitInterop.SelRegisterName("alloc"));
        var item = AppKitInterop.SendIntPtr_String_IntPtr_String(
            allocated,
            AppKitInterop.SelRegisterName("initWithTitle:action:keyEquivalent:"),
            title,
            action,
            string.Empty);

        if (action != IntPtr.Zero)
        {
            // [item setTarget:s_targetInstance]
            AppKitInterop.SendVoid_IntPtr(item, AppKitInterop.SelRegisterName("setTarget:"), s_targetInstance);
        }
        else
        {
            // Disable plain informational rows (unread count).
            AppKitInterop.SendVoid_IntPtr(item, AppKitInterop.SelRegisterName("setEnabled:"), IntPtr.Zero);
        }

        AppKitInterop.SendVoid_IntPtr(menu, AppKitInterop.SelRegisterName("addItem:"), item);
        // Issue #17: addItem: retains the item. Balance the +1 from alloc/init
        // by autoreleasing our reference; the menu owns the item now.
        AppKitInterop.SendVoid(item, AppKitInterop.SelRegisterName("autorelease"));
        return item;
    }

    private static void AddSeparator(IntPtr menu)
    {
        var itemClass = AppKitInterop.GetClass("NSMenuItem");
        var separator = AppKitInterop.SendIntPtr(itemClass, AppKitInterop.SelRegisterName("separatorItem"));
        AppKitInterop.SendVoid_IntPtr(menu, AppKitInterop.SelRegisterName("addItem:"), separator);
    }

    private static readonly object s_classRegistrationLock = new();

    private static void EnsureTargetClassRegistered()
    {
        // Issue #17: only set s_classRegistered = 1 *after* the registration
        // and method-add steps succeed, so a partial failure doesn't leave the
        // process in a state where a retry skips re-registration but the class
        // is unusable. We use a coarse lock here because class registration is
        // a one-time process-lifetime event.
        if (Volatile.Read(ref s_classRegistered) == 1)
        {
            return;
        }

        lock (s_classRegistrationLock)
        {
            if (s_classRegistered == 1)
            {
                return;
            }

            var nsObject = AppKitInterop.GetClass("NSObject");
            var cls = AppKitInterop.AllocateClassPair(nsObject, "GhuboonMenuTarget", IntPtr.Zero);

            try
            {
                var selShow = AppKitInterop.SelRegisterName("ghuboonShow:");
                var selHide = AppKitInterop.SelRegisterName("ghuboonHide:");
                var selSync = AppKitInterop.SelRegisterName("ghuboonSync:");
                var selSettings = AppKitInterop.SelRegisterName("ghuboonSettings:");
                var selQuit = AppKitInterop.SelRegisterName("ghuboonQuit:");

                // Type encoding for "void method(id self, SEL _cmd, id sender)":
                //   v   -> void return
                //   @   -> id self (16 bytes after the implicit args; the runtime
                //          accepts the simplified "v@:@" form for unary-arg actions)
                //   :   -> SEL _cmd
                //   @   -> id sender
                const string actionTypes = "v@:@";

                AppKitInterop.ClassAddMethod(cls, selShow, GetCallback(MenuAction.Show), actionTypes);
                AppKitInterop.ClassAddMethod(cls, selHide, GetCallback(MenuAction.Hide), actionTypes);
                AppKitInterop.ClassAddMethod(cls, selSync, GetCallback(MenuAction.Sync), actionTypes);
                AppKitInterop.ClassAddMethod(cls, selSettings, GetCallback(MenuAction.Settings), actionTypes);
                AppKitInterop.ClassAddMethod(cls, selQuit, GetCallback(MenuAction.Quit), actionTypes);

                AppKitInterop.RegisterClassPair(cls);

                // Allocate one shared target instance.
                var allocated = AppKitInterop.SendIntPtr(cls, AppKitInterop.SelRegisterName("alloc"));
                var instance = AppKitInterop.SendIntPtr(allocated, AppKitInterop.SelRegisterName("init"));

                // Publish the registered state only on full success.
                s_targetClass = cls;
                s_selShow = selShow;
                s_selHide = selHide;
                s_selSync = selSync;
                s_selSettings = selSettings;
                s_selQuit = selQuit;
                s_targetInstance = instance;
                Interlocked.Exchange(ref s_classRegistered, 1);
            }
            catch
            {
                // Best-effort: dispose the half-built class pair if registration
                // hasn't yet committed it to the runtime, so a retry can rebuild.
                try
                {
                    AppKitInterop.DisposeClassPair(cls);
                }
                catch
                {
                    // Ignore: the class may have already been registered, in
                    // which case dispose is a no-op (or invalid).
                }
                throw;
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ActionCallback(IntPtr self, IntPtr cmd, IntPtr sender);

    // Hold strong references so the GC doesn't collect the trampoline thunks
    // referenced by the live Objective-C class.
    private static readonly ActionCallback s_onShow = OnShow;
    private static readonly ActionCallback s_onHide = OnHide;
    private static readonly ActionCallback s_onSync = OnSync;
    private static readonly ActionCallback s_onSettings = OnSettings;
    private static readonly ActionCallback s_onQuit = OnQuit;

    private static IntPtr GetCallback(MenuAction action) => action switch
    {
        MenuAction.Show => Marshal.GetFunctionPointerForDelegate(s_onShow),
        MenuAction.Hide => Marshal.GetFunctionPointerForDelegate(s_onHide),
        MenuAction.Sync => Marshal.GetFunctionPointerForDelegate(s_onSync),
        MenuAction.Settings => Marshal.GetFunctionPointerForDelegate(s_onSettings),
        MenuAction.Quit => Marshal.GetFunctionPointerForDelegate(s_onQuit),
        _ => IntPtr.Zero,
    };

    private static void OnShow(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try { s_active?._context?.ShowMainWindow(); } catch { /* swallow into native frame */ }
    }

    private static void OnHide(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try { s_active?._context?.HideMainWindow(); } catch { /* swallow into native frame */ }
    }

    private static void OnSync(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try { s_active?._context?.SyncNow(); } catch { /* swallow into native frame */ }
    }

    private static void OnSettings(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try { s_active?._context?.OpenSettings(); } catch { /* swallow into native frame */ }
    }

    private static void OnQuit(IntPtr self, IntPtr cmd, IntPtr sender)
    {
        try { s_active?._context?.Quit(); } catch { /* swallow into native frame */ }
    }
}
