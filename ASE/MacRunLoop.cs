using System;
using System.Runtime.InteropServices;

namespace ASE
{
    /// <summary>
    /// Tells whether the macOS main run loop is in its default mode, which is the only one in
    /// which <c>SDL_PumpEvents</c> may run without breaking the window.
    /// <para>The title-bar buttons, edge resizing and window dragging are each a nested
    /// <em>tracking</em> loop inside Cocoa, waiting for its own mouseDragged/mouseUp. Avalonia's
    /// dispatcher timers keep firing there (common modes), and SDL's Cocoa pump dequeues
    /// <em>every</em> pending event with <c>nextEventMatchingMask:…dequeue:YES</c> — so the 4 ms
    /// pump timer took the mouse-up away from the close button and the drags away from the
    /// resize, and neither ever completed. Avalonia's own menu draws in the client area with no
    /// nested loop, which is why it kept working.</para>
    /// </summary>
    internal static class MacRunLoop
    {
        private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(CoreFoundation)] private static extern IntPtr CFRunLoopGetCurrent();
        [DllImport(CoreFoundation)] private static extern IntPtr CFRunLoopCopyCurrentMode(IntPtr runLoop);
        [DllImport(CoreFoundation)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool CFEqual(IntPtr a, IntPtr b);
        [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr obj);

        // kCFRunLoopDefaultMode is an exported CFStringRef variable, not a function: read its value once.
        private static readonly IntPtr DefaultMode =
            Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(CoreFoundation), "kCFRunLoopDefaultMode"));

        /// <summary>True when the current thread's run loop is in kCFRunLoopDefaultMode (or not running).</summary>
        public static bool InDefaultMode()
        {
            IntPtr mode = CFRunLoopCopyCurrentMode(CFRunLoopGetCurrent());
            if (mode == IntPtr.Zero)
                return true;

            bool isDefault = CFEqual(mode, DefaultMode);
            CFRelease(mode);
            return isDefault;
        }
    }
}
