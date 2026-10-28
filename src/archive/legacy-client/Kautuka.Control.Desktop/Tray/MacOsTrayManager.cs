using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Kautuka.Control.Core.Tray;

namespace Kautuka.Control.Desktop;

/// <summary>
/// macOS system tray via NSStatusItem through libobjc.dylib.
/// Uses Objective-C message send (objc_msgSend) — the same approach as .NET MAUI.
/// AOT-safe: pure P/Invoke, no reflection.
/// Menu item callbacks are wired via a registered ObjC class (KautukaMenuDelegate)
/// whose IMP is an [UnmanagedCallersOnly] static method — AOT-compatible.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacOsTrayManager : ITrayManager
{
    private static readonly Action<ILogger, string, Exception?> logTrayInitFailed = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(0, nameof(logTrayInitFailed)),
        "macOS tray init failed: {Message}");

    private readonly ILogger log;
    private IntPtr statusItem;
    private IntPtr delegateInstance;
    private TrayMenuItem[] menu = [];
    private string baseTooltip = "Kautuka";

    // Static dispatch table: delegate instance pointer → menu array.
    // Written on Show(), removed on Dispose(). Accessed from the ObjC IMP callback.
    private static readonly ConcurrentDictionary<IntPtr, TrayMenuItem[]> dispatch = new();

    // Ensures the ObjC class is registered only once across multiple instances.
    private static bool classRegistered;
    private static readonly object classLock = new();

    public MacOsTrayManager(ILogger log) => this.log = log;

    ~MacOsTrayManager() => Hide();

    public void Show(string tooltip, TrayMenuItem[] menu, Action? onActivate = null)
    {
        baseTooltip = tooltip.Length > 20 ? "Kautuka" : tooltip;
        this.menu = menu;
        try
        {
            EnsureDelegateClassRegistered();

            var statusBar = objc_msgSend(
                objc_getClass("NSStatusBar"),
                sel_registerName("systemStatusBar"));

            statusItem = objc_msgSend_val(statusBar,
                sel_registerName("statusItemWithLength:"),
                -1.0); // NSVariableStatusItemLength

            // Retain the status item so it outlives the autorelease pool
            objc_msgSend(statusItem, sel_registerName("retain"));

            SetBrandMark();
            objc_msgSend(statusItem, sel_registerName("setToolTip:"), NSString(baseTooltip));
            UpdateStatus(true);

            // Create the delegate instance and register it in the dispatch table
            var delegateClass = objc_getClass("KautukaMenuDelegate");
            delegateInstance = objc_msgSend(
                objc_msgSend(delegateClass, sel_registerName("alloc")),
                sel_registerName("init"));
            dispatch[delegateInstance] = menu;

            // Build menu — wire each item's action to the delegate
            var nsMenu = objc_msgSend(
                objc_getClass("NSMenu"),
                sel_registerName("new"));

            var actionSel = sel_registerName("menuItemClicked:");
            var tagSel = sel_registerName("setTag:");
            var actionSet = sel_registerName("setAction:");
            var targetSet = sel_registerName("setTarget:");
            var addItem = sel_registerName("addItem:");

            var tagIndex = 0;
            for (var i = 0; i < menu.Length; i++)
            {
                var item = menu[i];
                if (item.IsSeparator)
                {
                    var sep = objc_msgSend(
                        objc_getClass("NSMenuItem"),
                        sel_registerName("separatorItem"));
                    objc_msgSend(nsMenu, addItem, sep);
                }
                else
                {
                    tagIndex++;
                    var nsItemTitle = NSString(item.Label ?? string.Empty);
                    // initWithTitle:action:keyEquivalent: — pass actionSel + empty key equiv
                    var menuItem = objc_msgSend_alloc3(
                        objc_msgSend(objc_getClass("NSMenuItem"), sel_registerName("alloc")),
                        sel_registerName("initWithTitle:action:keyEquivalent:"),
                        nsItemTitle,
                        actionSel,
                        NSString(string.Empty));

                    // Set 1-based tag for dispatch lookup
                    objc_msgSend_long(menuItem, tagSel, (IntPtr)tagIndex);
                    // Wire the target to our delegate instance
                    objc_msgSend(menuItem, targetSet, delegateInstance);
                    objc_msgSend(nsMenu, addItem, menuItem);
                }
            }

            objc_msgSend(statusItem, sel_registerName("setMenu:"), nsMenu);
        }
        catch (DllNotFoundException ex)
        {
            logTrayInitFailed(log, ex.Message, ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            logTrayInitFailed(log, ex.Message, ex);
        }
        catch (InvalidOperationException ex)
        {
            logTrayInitFailed(log, ex.Message, ex);
        }
        catch (SEHException ex)
        {
            logTrayInitFailed(log, ex.Message, ex);
        }
    }

    public void Hide()
    {
        if (statusItem != IntPtr.Zero)
        {
            try
            {
                var statusBar = objc_msgSend(
                    objc_getClass("NSStatusBar"),
                    sel_registerName("systemStatusBar"));
                objc_msgSend(statusBar, sel_registerName("removeStatusItem:"), statusItem);
                objc_msgSend(statusItem, sel_registerName("release"));
                statusItem = IntPtr.Zero;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (InvalidOperationException) { }
            catch (SEHException) { }
        }
        if (delegateInstance != IntPtr.Zero)
        {
            dispatch.TryRemove(delegateInstance, out _);
            objc_msgSend(delegateInstance, sel_registerName("release"));
            delegateInstance = IntPtr.Zero;
        }
    }

    public void UpdateStatus(bool gatewayOnline)
    {
        if (statusItem == IntPtr.Zero) return;
        try
        {
            // Keep gateway status visible beside the shared Kautuka mark.
            var indicator = gatewayOnline ? "●" : "○";
            var nsTitle = NSString(indicator);
            objc_msgSend(statusItem, sel_registerName("setTitle:"), nsTitle);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (InvalidOperationException) { }
        catch (SEHException) { }
    }

    private void SetBrandMark()
    {
        var imagePath = Path.Combine(AppContext.BaseDirectory, "assets", "kautuka-mark.png");
        var image = objc_msgSend(
            objc_getClass("NSImage"),
            sel_registerName("imageWithContentsOfFile:"),
            NSString(imagePath));
        if (image == IntPtr.Zero)
            throw new InvalidOperationException($"Kautuka tray mark was not found at {imagePath}.");

        var button = objc_msgSend(statusItem, sel_registerName("button"));
        objc_msgSend_size(image, sel_registerName("setSize:"), new CGSize(18, 18));
        objc_msgSend(button, sel_registerName("setImage:"), image);
    }

    /// <summary>No-op: macOS balloon notifications require a signed app bundle with entitlements.</summary>
    public void ShowBalloon(string title, string message) { }

    public void Dispose()
    {
        Hide();
        GC.SuppressFinalize(this);
    }

    // ── ObjC delegate class registration ──────────────────────────────────────

    private static void EnsureDelegateClassRegistered()
    {
        if (classRegistered) return;
        lock (classLock)
        {
            if (classRegistered) return;

            var nsObject = objc_getClass("NSObject");
            var cls = objc_allocateClassPair(nsObject, "KautukaMenuDelegate", 0);
            if (cls == IntPtr.Zero)
            {
                // Class may already exist from a previous app run in the same process — look it up
                cls = objc_getClass("KautukaMenuDelegate");
                if (cls == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to allocate KautukaMenuDelegate ObjC class.");
            }
            else
            {
                // Add the IMP for menuItemClicked: — function pointer is stable because it's [UnmanagedCallersOnly]
                unsafe
                {
                    var imp = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MenuItemClickedImp;
                    class_addMethod(cls, sel_registerName("menuItemClicked:"), imp, "v@:@");
                }
                objc_registerClassPair(cls);
            }

            classRegistered = true;
        }
    }

    /// <summary>
    /// ObjC IMP for menuItemClicked: — called by the AppKit runtime on the main thread.
    /// self = delegate instance, sel = selector, sender = NSMenuItem*.
    /// Reads the tag from sender, looks up and invokes the corresponding menu item callback.
    /// Must be a static method with [UnmanagedCallersOnly] for AOT-safe function pointer emission.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuItemClickedImp(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            var tag = objc_msgSend_get_long(sender, sel_registerName("tag")).ToInt32();
            if (dispatch.TryGetValue(self, out var menu) && tag > 0 && tag <= menu.Length)
                menu[tag - 1].OnClick?.Invoke();
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        catch (InvalidOperationException) { }
        catch (SEHException) { }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static IntPtr NSString(string s)
    {
        var cls = objc_getClass("NSString");
        return objc_msgSend_str(cls,
            sel_registerName("stringWithUTF8String:"), s);
    }

    // ── P/Invoke ───────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGSize(double Width, double Height);

    [LibraryImport("/usr/lib/libobjc.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_val(IntPtr receiver, IntPtr selector, double arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_size(IntPtr receiver, IntPtr selector, CGSize size);

    [LibraryImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_msgSend_str(IntPtr receiver, IntPtr selector, string arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_alloc3(IntPtr receiver, IntPtr selector,
        IntPtr a1, IntPtr a2, IntPtr a3);

    // Set an NSInteger (long) property on an ObjC object
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_long(IntPtr receiver, IntPtr selector, IntPtr value);

    // Read an NSInteger (long) from an ObjC object
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_get_long(IntPtr receiver, IntPtr selector);

    [LibraryImport("/usr/lib/libobjc.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr sel_registerName(string name);

    [LibraryImport("/usr/lib/libobjc.dylib", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);

    [DllImport("/usr/lib/libobjc.dylib")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [LibraryImport("/usr/lib/libobjc.dylib", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);
}

