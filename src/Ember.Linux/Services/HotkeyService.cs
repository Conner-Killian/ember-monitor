using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Input;
using Ember.Services.Linux;

namespace Ember.Services;

/// <summary>
/// Global hotkey for X11 sessions, using XInput2 raw key events (seen no matter which window has
/// focus, and nothing is grabbed, so other apps still get the keys). Same chord rules as Windows:
///  • combo with a normal key  → fires when the final key goes down while exactly the combo is held;
///  • modifier-only combo      → fires on release, only if no other key was pressed during the chord.
/// Keys are stored as Windows virtual-key codes so settings and descriptions match the Windows app.
/// In Wayland sessions apps can't see global keys; see GnomeShortcut / the Overlay page.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly object _lock = new();
    private readonly HashSet<int> _down = new();
    private HashSet<int> _combo = new();
    private bool _chordDirty, _comboArmed, _firedThisChord;
    private Thread? _thread;
    private volatile bool _running;
    private IntPtr _dpy;

    /// <summary>Raised on the listener thread.</summary>
    public event Action? Pressed;

    /// <summary>Global listening is possible (X11 session with XInput2).</summary>
    public bool IsActive { get; private set; }

    public void SetCombo(IEnumerable<int> vks)
    {
        lock (_lock) { _combo = new HashSet<int>(vks); _down.Clear(); _comboArmed = _chordDirty = _firedThisChord = false; }
    }

    public void Install()
    {
        if (_thread != null || !OperatingSystem.IsLinux() || Desktop.IsWayland) return;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return;
        try
        {
            _dpy = XOpenDisplay(IntPtr.Zero);
            if (_dpy == IntPtr.Zero) return;
            if (!XQueryExtension(_dpy, "XInputExtension", out _xiOpcode, out _, out _)) return;
            int major = 2, minor = 2; // 2.1+ delivers raw events even while another app holds a grab
            if (XIQueryVersion(_dpy, ref major, ref minor) != 0) return;
            var root = XDefaultRootWindow(_dpy);
            var mask = new byte[4];
            mask[XI_RawKeyPress >> 3] |= (byte)(1 << (XI_RawKeyPress & 7));
            mask[XI_RawKeyRelease >> 3] |= (byte)(1 << (XI_RawKeyRelease & 7));
            var h = GCHandle.Alloc(mask, GCHandleType.Pinned);
            try
            {
                var em = new XIEventMask { deviceid = 1 /* XIAllMasterDevices */, mask_len = mask.Length, mask = h.AddrOfPinnedObject() };
                XISelectEvents(_dpy, root, ref em, 1);
            }
            finally { h.Free(); }
            XFlush(_dpy);
        }
        catch { return; }
        IsActive = true;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Ember.Hotkey" };
        _thread.Start();
    }

    private int _xiOpcode;

    private void Loop()
    {
        var ev = Marshal.AllocHGlobal(192);
        try
        {
            while (_running)
            {
                XNextEvent(_dpy, ev);
                // XGenericEventCookie: type@0, extension@32, evtype@36
                if (Marshal.ReadInt32(ev, 0) != GenericEvent || Marshal.ReadInt32(ev, 32) != _xiOpcode) continue;
                if (!XGetEventData(_dpy, ev)) continue;
                try
                {
                    int evtype = Marshal.ReadInt32(ev, 36);
                    var data = Marshal.ReadIntPtr(ev, 48);
                    if (data == IntPtr.Zero) continue;
                    int keycode = Marshal.ReadInt32(data, 56); // XIRawEvent.detail
                    int vk = KeysymToVk((long)XkbKeycodeToKeysym(_dpy, (byte)keycode, 0, 0));
                    bool fire;
                    lock (_lock) fire = _combo.Count > 0 && Handle(vk, evtype == XI_RawKeyPress, evtype == XI_RawKeyRelease);
                    if (fire) Pressed?.Invoke();
                }
                finally { XFreeEventData(_dpy, ev); }
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(ev); }
    }

    private bool Handle(int vk, bool down, bool up)
    {
        if (down)
        {
            bool repeat = _down.Contains(vk);
            _down.Add(vk);
            if (repeat) return false;
            if (!_combo.Contains(vk)) { _chordDirty = true; return false; }
            if (_down.SetEquals(_combo))
            {
                _comboArmed = true;
                if (!ComboIsModifierOnly && !_firedThisChord) { _firedThisChord = true; return true; }
            }
        }
        else if (up)
        {
            bool fire = false;
            if (ComboIsModifierOnly && _comboArmed && !_chordDirty && !_firedThisChord && _combo.Contains(vk)) { _firedThisChord = true; fire = true; }
            _down.Remove(vk);
            if (_down.Count == 0) _comboArmed = _chordDirty = _firedThisChord = false;
            return fire;
        }
        return false;
    }

    private bool ComboIsModifierOnly => _combo.Count > 0 && _combo.All(IsModifier);
    public static bool IsModifier(int vk) => vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    public void Dispose() { _running = false; }

    // ------------------------------------------------------------------ key codes

    /// <summary>X keysym → Windows virtual-key code (so the Windows default Right Ctrl + Right Alt works).</summary>
    public static int KeysymToVk(long ks) => ks switch
    {
        0xffe1 => 0xA0, 0xffe2 => 0xA1, 0xffe3 => 0xA2, 0xffe4 => 0xA3,
        0xffe9 => 0xA4, 0xffea => 0xA5, 0xfe03 => 0xA5, // AltGr reports ISO_Level3_Shift
        0xffeb => 0x5B, 0xffec => 0x5C, 0xff67 => 0x5D,
        >= 0x61 and <= 0x7a => (int)(ks - 0x20), >= 0x41 and <= 0x5a => (int)ks, >= 0x30 and <= 0x39 => (int)ks,
        >= 0xffbe and <= 0xffd5 => (int)(0x70 + ks - 0xffbe),
        >= 0xffb0 and <= 0xffb9 => (int)(0x60 + ks - 0xffb0),
        0x20 => 0x20, 0xff0d => 0x0D, 0xff1b => 0x1B, 0xff09 => 0x09, 0xff08 => 0x08,
        0xff63 => 0x2D, 0xffff => 0x2E, 0xff50 => 0x24, 0xff57 => 0x23, 0xff55 => 0x21, 0xff56 => 0x22,
        0xff51 => 0x25, 0xff52 => 0x26, 0xff53 => 0x27, 0xff54 => 0x28,
        0xff13 => 0x13, 0xff14 => 0x91, 0xff61 => 0x2C,
        _ => 0x10000 + (int)(ks & 0xFFFF),
    };

    /// <summary>Avalonia key (from the window's own key events, used when recording) → virtual-key code.</summary>
    public static int KeyToVk(Key k) => k switch
    {
        Key.LeftShift => 0xA0, Key.RightShift => 0xA1, Key.LeftCtrl => 0xA2, Key.RightCtrl => 0xA3,
        Key.LeftAlt => 0xA4, Key.RightAlt => 0xA5, Key.LWin => 0x5B, Key.RWin => 0x5C, Key.Apps => 0x5D,
        >= Key.A and <= Key.Z => 0x41 + (k - Key.A),
        >= Key.D0 and <= Key.D9 => 0x30 + (k - Key.D0),
        >= Key.F1 and <= Key.F24 => 0x70 + (k - Key.F1),
        >= Key.NumPad0 and <= Key.NumPad9 => 0x60 + (k - Key.NumPad0),
        Key.Space => 0x20, Key.Enter => 0x0D, Key.Escape => 0x1B, Key.Tab => 0x09, Key.Back => 0x08,
        Key.Insert => 0x2D, Key.Delete => 0x2E, Key.Home => 0x24, Key.End => 0x23, Key.PageUp => 0x21, Key.PageDown => 0x22,
        Key.Left => 0x25, Key.Up => 0x26, Key.Right => 0x27, Key.Down => 0x28,
        Key.Pause => 0x13, Key.Scroll => 0x91, Key.PrintScreen => 0x2C,
        _ => 0,
    };

    /// <summary>Avalonia key → GNOME accelerator key name (null for modifiers / unsupported keys).</summary>
    public static string? KeyToGtkName(Key k) => k switch
    {
        >= Key.A and <= Key.Z => ((char)('a' + (k - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => "F" + (1 + (k - Key.F1)),
        Key.Space => "space", Key.Insert => "Insert", Key.Delete => "Delete", Key.Home => "Home", Key.End => "End",
        Key.PageUp => "Page_Up", Key.PageDown => "Page_Down", Key.Pause => "Pause", Key.PrintScreen => "Print",
        Key.Left => "Left", Key.Right => "Right", Key.Up => "Up", Key.Down => "Down",
        _ => null,
    };

    public static string Describe(IEnumerable<int> vks)
    {
        var list = vks.OrderBy(v => IsModifier(v) ? 0 : 1).ThenBy(v => v).ToList();
        return list.Count == 0 ? "Not set" : string.Join(" + ", list.Select(KeyName));
    }

    public static string KeyName(int vk) => vk switch
    {
        0xA0 => "Left Shift", 0xA1 => "Right Shift", 0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl",
        0xA4 => "Left Alt", 0xA5 => "Right Alt", 0x5B => "Left Super", 0x5C => "Right Super", 0x5D => "Menu",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(), >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc", 0x09 => "Tab", 0x08 => "Backspace",
        0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End", 0x21 => "Page Up", 0x22 => "Page Down",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x13 => "Pause", 0x91 => "Scroll Lock", 0x2C => "Print Screen",
        _ => $"Key {vk:X2}",
    };

    // ------------------------------------------------------------------ Xlib / XInput2
    private const string LibX11 = "libX11.so.6", LibXi = "libXi.so.6";
    private const int GenericEvent = 35, XI_RawKeyPress = 13, XI_RawKeyRelease = 14;

    [StructLayout(LayoutKind.Sequential)] private struct XIEventMask { public int deviceid; public int mask_len; public IntPtr mask; }

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr dpy);
    [DllImport(LibX11)] private static extern bool XQueryExtension(IntPtr dpy, string name, out int opcode, out int ev, out int err);
    [DllImport(LibX11)] private static extern int XNextEvent(IntPtr dpy, IntPtr ev);
    [DllImport(LibX11)] private static extern bool XGetEventData(IntPtr dpy, IntPtr cookie);
    [DllImport(LibX11)] private static extern void XFreeEventData(IntPtr dpy, IntPtr cookie);
    [DllImport(LibX11)] private static extern int XFlush(IntPtr dpy);
    [DllImport(LibX11)] private static extern ulong XkbKeycodeToKeysym(IntPtr dpy, byte keycode, int group, int level);
    [DllImport(LibXi)] private static extern int XIQueryVersion(IntPtr dpy, ref int major, ref int minor);
    [DllImport(LibXi)] private static extern int XISelectEvents(IntPtr dpy, IntPtr win, ref XIEventMask masks, int n);
}
