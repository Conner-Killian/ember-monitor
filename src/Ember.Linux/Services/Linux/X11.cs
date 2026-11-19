using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Ember.Services.Linux;

/// <summary>
/// The few X11 window tweaks Avalonia doesn't expose. Ember's windows are X11 windows (native in an
/// X11 session, via XWayland in a Wayland session), so these work on GNOME, KDE, Xfce, Cinnamon…
/// </summary>
public static class X11
{
    private const string LibX11 = "libX11.so.6";
    private const string LibXext = "libXext.so.6";

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] private static extern IntPtr XInternAtom(IntPtr dpy, string name, bool onlyIfExists);
    [DllImport(LibX11)] private static extern int XChangeProperty(IntPtr dpy, IntPtr w, IntPtr prop, IntPtr type, int format, int mode, byte[] data, int n);
    [DllImport(LibX11)] private static extern int XChangeProperty(IntPtr dpy, IntPtr w, IntPtr prop, IntPtr type, int format, int mode, IntPtr[] data, int n);
    [DllImport(LibX11)] private static extern int XSendEvent(IntPtr dpy, IntPtr w, bool propagate, IntPtr mask, ref XClientMessageEvent ev);
    [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr dpy);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr dpy);
    [DllImport(LibXext)] private static extern void XShapeCombineRectangles(IntPtr dpy, IntPtr w, int kind, int x, int y, IntPtr rects, int n, int op, int ordering);
    [DllImport(LibXext)] private static extern void XShapeCombineMask(IntPtr dpy, IntPtr w, int kind, int x, int y, IntPtr pixmap, int op);

    [StructLayout(LayoutKind.Sequential, Size = 192)]
    private struct XClientMessageEvent
    {
        public int type; public IntPtr serial; public int send_event; public IntPtr display; public IntPtr window;
        public IntPtr message_type; public int format; public IntPtr l0, l1, l2, l3, l4;
    }

    private const int ClientMessage = 33, PropModeReplace = 0, ShapeInput = 2, ShapeSet = 0;
    private static readonly IntPtr SubstructureMask = (IntPtr)((1 << 19) | (1 << 20)); // Redirect | Notify

    private static readonly IntPtr AllDesktops = new(0xFFFFFFFFL); // _NET_WM_DESKTOP: every workspace
    private static IntPtr _dpy;
    private static bool _failed;

    /// <summary>True in an X11 session or on XWayland (DISPLAY set) — i.e. whenever Avalonia uses X11.</summary>
    public static bool Available
    {
        get
        {
            if (_dpy != IntPtr.Zero) return true;
            if (_failed || !OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return false;
            try { _dpy = XOpenDisplay(IntPtr.Zero); } catch { }
            _failed = _dpy == IntPtr.Zero;
            return !_failed;
        }
    }

    private static IntPtr Atom(string n) => XInternAtom(_dpy, n, false);

    /// <summary>Asks GTK-themed window managers (GNOME/Mutter, Xfce…) for the dark title bar variant.</summary>
    public static void PreferDarkTitleBar(IntPtr window)
    {
        if (!Available || window == IntPtr.Zero) return;
        var v = Encoding.UTF8.GetBytes("dark");
        XChangeProperty(_dpy, window, Atom("_GTK_THEME_VARIANT"), Atom("UTF8_STRING"), 8, PropModeReplace, v, v.Length);
        XFlush(_dpy);
    }

    /// <summary>
    /// Overlay window type, set before the window is mapped: a "dock" stays above normal windows on
    /// every workspace, never takes focus and has no taskbar / Alt+Tab entry.
    /// </summary>
    public static void MarkAsOverlay(IntPtr window)
    {
        if (!Available || window == IntPtr.Zero) return;
        XChangeProperty(_dpy, window, Atom("_NET_WM_WINDOW_TYPE"), Atom("ATOM"), 32, PropModeReplace,
            new[] { Atom("_NET_WM_WINDOW_TYPE_DOCK") }, 1);
        XChangeProperty(_dpy, window, Atom("_NET_WM_STATE"), Atom("ATOM"), 32, PropModeReplace,
            new[] { Atom("_NET_WM_STATE_ABOVE"), Atom("_NET_WM_STATE_STICKY"), Atom("_NET_WM_STATE_SKIP_TASKBAR"), Atom("_NET_WM_STATE_SKIP_PAGER") }, 4);
        XChangeProperty(_dpy, window, Atom("_NET_WM_DESKTOP"), Atom("CARDINAL"), 32, PropModeReplace, new[] { AllDesktops }, 1);
        XFlush(_dpy);
    }

    /// <summary>After mapping: re-request "above, all workspaces" from the window manager.</summary>
    public static void KeepOnTop(IntPtr window)
    {
        if (!Available || window == IntPtr.Zero) return;
        var root = XDefaultRootWindow(_dpy);
        void Send(IntPtr type, IntPtr a, IntPtr b, IntPtr c)
        {
            var ev = new XClientMessageEvent { type = ClientMessage, send_event = 1, display = _dpy, window = window, message_type = type, format = 32, l0 = a, l1 = b, l2 = c, l3 = (IntPtr)1 };
            XSendEvent(_dpy, root, false, SubstructureMask, ref ev);
        }
        var state = Atom("_NET_WM_STATE");
        Send(state, (IntPtr)1, Atom("_NET_WM_STATE_ABOVE"), Atom("_NET_WM_STATE_STICKY"));
        Send(state, (IntPtr)1, Atom("_NET_WM_STATE_SKIP_TASKBAR"), Atom("_NET_WM_STATE_SKIP_PAGER"));
        Send(Atom("_NET_WM_DESKTOP"), AllDesktops, IntPtr.Zero, IntPtr.Zero);
        XFlush(_dpy);
    }

    /// <summary>Click-through: an empty input shape lets every click fall through to the window below.</summary>
    public static void SetClickThrough(IntPtr window, bool clickThrough)
    {
        if (!Available || window == IntPtr.Zero) return;
        try
        {
            if (clickThrough) XShapeCombineRectangles(_dpy, window, ShapeInput, 0, 0, IntPtr.Zero, 0, ShapeSet, 0);
            else XShapeCombineMask(_dpy, window, ShapeInput, 0, 0, IntPtr.Zero, ShapeSet); // back to the full window
            XFlush(_dpy);
        }
        catch (DllNotFoundException) { /* libXext missing: overlay just isn't click-through */ }
    }
}
