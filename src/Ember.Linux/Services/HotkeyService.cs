using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Input;
using Ember.Services.Linux;

namespace Ember.Services;

/// <summary>
/// Global hotkeys (show / hide and every overlay preset), working like the Windows app. Keys come from
/// XInput2 raw events on X11, or straight from the keyboard devices (evdev) on Wayland, once the
/// user has allowed it (see the evdev section). Either way nothing is grabbed, so other apps still get
/// the keys. Same chord rules as Windows:
///  • combo with a normal key  → fires when the final key goes down while exactly the combo is held;
///  • modifier-only combo      → fires on release, only if no other key was pressed during the chord.
/// Keys are stored as Windows virtual-key codes so settings and descriptions match the Windows app.
/// Without either source (Wayland before allowing it), see GnomeShortcut / the Overlay page.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly object _lock = new();
    private readonly HashSet<int> _down = new();
    private List<Binding> _bindings = new();
    private Thread? _thread;
    private volatile bool _running;
    private IntPtr _dpy;

    /// <summary>One hotkey: the main show / hide key (id "") or a preset's (its id).</summary>
    private sealed class Binding
    {
        public required string Id { get; init; }
        public required HashSet<int> Combo { get; init; }
        public bool Dirty, Armed, Fired;
        public bool ModifierOnly => Combo.All(IsModifier);
        public void Reset() => Dirty = Armed = Fired = false;
    }

    /// <summary>Raised on the listener thread with the hotkey's id ("" = show / hide, else a preset id).</summary>
    public event Action<string>? Pressed;

    /// <summary>Global hotkeys work: X11 with XInput2, or the keyboard devices are readable (evdev).</summary>
    public bool IsActive { get; private set; }

    /// <summary>Keys come straight from the keyboard devices (Wayland, after the one-time permission).</summary>
    public bool UsesEvdev { get; private set; }

    /// <summary>
    /// Wayland: keyboards are there but this user may not read them yet. The Overlay page offers the
    /// one-time permission (<see cref="EnableEvdevAsync"/>); until then, hotkeys can't be global.
    /// </summary>
    public bool EvdevBlocked { get; private set; }

    /// <summary>
    /// While set (recording a hotkey on the Overlay page), keys are passed here on the UI thread
    /// instead of triggering anything. Used with evdev so a recorded hotkey is exactly what the
    /// reader sees (physical keys, whatever the keyboard layout).
    /// </summary>
    public Action<int, bool>? Recorder { get; set; }

    /// <summary>The hotkeys to watch: id → key combination ("" = show / hide overlay, else a preset id). Empty combos are skipped.</summary>
    public void SetBindings(IEnumerable<(string id, IEnumerable<int> combo)> bindings)
    {
        var list = bindings.Select(b => new Binding { Id = b.id, Combo = new HashSet<int>(b.combo) })
                           .Where(b => b.Combo.Count > 0).ToList();
        lock (_lock)
        {
            if (list.Count == _bindings.Count && list.Zip(_bindings).All(z => z.First.Id == z.Second.Id && z.First.Combo.SetEquals(z.Second.Combo))) return;
            _bindings = list;
            _down.Clear();
        }
    }

    /// <summary>
    /// X11: XInput2 raw events (no setup needed). Wayland, or X11 without XInput2: the keyboard
    /// devices themselves, if this user may read them.
    /// </summary>
    public void Install()
    {
        if (IsActive || !OperatingSystem.IsLinux()) return;
        if (!Desktop.IsWayland && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && TryStartXInput()) return;
        TryStartEvdev();
    }

    private bool TryStartXInput()
    {
        try
        {
            _dpy = XOpenDisplay(IntPtr.Zero);
            if (_dpy == IntPtr.Zero) return false;
            if (!XQueryExtension(_dpy, "XInputExtension", out _xiOpcode, out _, out _)) return false;
            int major = 2, minor = 2; // 2.1+ delivers raw events even while another app holds a grab
            if (XIQueryVersion(_dpy, ref major, ref minor) != 0) return false;
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
        catch { return false; }
        IsActive = true;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Ember.Hotkey" };
        _thread.Start();
        return true;
    }

    /// <summary>A key from either source: recorded (Overlay page) or matched against the hotkeys.</summary>
    private void OnKey(int vk, bool down, bool up)
    {
        if (Recorder is { } rec) { Avalonia.Threading.Dispatcher.UIThread.Post(() => rec(vk, down)); return; }
        string? fired;
        lock (_lock) fired = _bindings.Count > 0 ? Handle(vk, down, up) : null;
        if (fired != null) Pressed?.Invoke(fired);
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
                    OnKey(vk, evtype == XI_RawKeyPress, evtype == XI_RawKeyRelease);
                }
                finally { XFreeEventData(_dpy, ev); }
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(ev); }
    }

    /// <summary>Tracks the chord; returns the id of the hotkey that should fire, if any.</summary>
    private string? Handle(int vk, bool down, bool up)
    {
        string? fired = null;
        if (down)
        {
            bool repeat = _down.Contains(vk);
            _down.Add(vk);
            if (repeat) return null;
            foreach (var b in _bindings)
            {
                if (!b.Combo.Contains(vk)) { b.Dirty = true; continue; }
                if (!_down.SetEquals(b.Combo)) continue;
                b.Armed = true;
                if (!b.ModifierOnly && !b.Fired) { b.Fired = true; fired ??= b.Id; }
            }
        }
        else if (up)
        {
            foreach (var b in _bindings)
                if (b.ModifierOnly && b.Armed && !b.Dirty && !b.Fired && b.Combo.Contains(vk)) { b.Fired = true; fired ??= b.Id; }
            _down.Remove(vk);
            if (_down.Count == 0) foreach (var b in _bindings) b.Reset();
        }
        return fired;
    }

    public static bool IsModifier(int vk) => vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;

    public void Dispose()
    {
        _running = false;
        _rescan?.Dispose();
    }

    // ------------------------------------------------------------------ evdev (Wayland)
    // Wayland gives apps no global keys, so the keyboards are read directly (/dev/input/event*),
    // like the Windows low-level hook: every key, either side's modifiers, nothing swallowed.
    // Reading them needs permission once: a udev rule that gives the user at the screen access to
    // keyboards (systemd-logind "uaccess"), added by EnableEvdevAsync through pkexec.

    public const string RulePath = "/etc/udev/rules.d/70-ember-monitor-hotkeys.rules";
    public const string UdevRule = "SUBSYSTEM==\"input\", KERNEL==\"event*\", ENV{ID_INPUT_KEYBOARD}==\"1\", TAG+=\"uaccess\"";

    /// <summary>The same steps as a terminal command, for when pkexec isn't available.</summary>
    public static string EnableCommand =>
        $"echo '{UdevRule}' | sudo tee {RulePath} && sudo udevadm control --reload-rules && sudo udevadm trigger --subsystem-match=input";

    private readonly object _devLock = new();
    private readonly HashSet<string> _readers = new();
    private Timer? _rescan;

    /// <summary>Keyboard event devices, from /proc/bus/input/devices (handler "kbd", key events and auto-repeat).</summary>
    public static List<string> KeyboardDevices()
    {
        var list = new List<string>();
        try
        {
            foreach (var block in System.IO.File.ReadAllText("/proc/bus/input/devices").Split("\n\n"))
            {
                var handlers = block.Split('\n').FirstOrDefault(l => l.StartsWith("H: Handlers="))?["H: Handlers=".Length..].Split(' ') ?? Array.Empty<string>();
                var ev = block.Split('\n').FirstOrDefault(l => l.StartsWith("B: EV="))?["B: EV=".Length..].Trim();
                if (!handlers.Contains("kbd") || ev == null || !ulong.TryParse(ev, System.Globalization.NumberStyles.HexNumber, null, out var bits)) continue;
                const ulong EV_KEY = 1 << 1, EV_REP = 1 << 20; // power buttons etc. have no auto-repeat
                if ((bits & EV_KEY) == 0 || (bits & EV_REP) == 0) continue;
                if (handlers.FirstOrDefault(h => h.StartsWith("event")) is { } e) list.Add("/dev/input/" + e);
            }
        }
        catch { }
        return list;
    }

    private static bool CanRead(string path)
    {
        try { using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite, 1); return true; }
        catch { return false; }
    }

    /// <summary>
    /// Raised (on a timer thread) when <see cref="IsActive"/> or <see cref="EvdevBlocked"/> changes after
    /// startup: a keyboard appeared, access was granted, or a keyboard turned up that may not be read yet.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>Starts reading the keyboards if this user may. Returns whether global hotkeys now work.</summary>
    public bool TryStartEvdev()
    {
        if (!OperatingSystem.IsLinux()) return false;
        lock (_devLock)
        {
            if (UsesEvdev) return true;
            var keyboards = KeyboardDevices();
            var readable = keyboards.Where(CanRead).ToList();
            EvdevBlocked = readable.Count == 0 && keyboards.Count > 0;
            // Every few seconds: keyboards plugged in later (Bluetooth, a dock), and — while there's
            // none we may read — whether one has appeared or access was granted meanwhile.
            _rescan ??= new Timer(_ => Rescan(), null, 3000, 3000);
            if (readable.Count == 0) return false;
            _running = true;
            UsesEvdev = IsActive = true;
        }
        foreach (var d in KeyboardDevices().Where(CanRead)) StartReader(d);
        return true;
    }

    private void Rescan()
    {
        if (UsesEvdev) { foreach (var d in KeyboardDevices()) StartReader(d); return; }
        bool wasBlocked = EvdevBlocked;
        if (TryStartEvdev() || EvdevBlocked != wasBlocked) StateChanged?.Invoke();
    }

    private void StartReader(string path)
    {
        lock (_devLock) if (!_readers.Add(path)) return;
        new Thread(() =>
        {
            try
            {
                using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite, 1);
                var buf = new byte[24]; // struct input_event on 64-bit: timeval (16), type (2), code (2), value (4)
                while (_running)
                {
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int n = fs.Read(buf, got, buf.Length - got);
                        if (n <= 0) return; // device gone
                        got += n;
                    }
                    ushort type = BitConverter.ToUInt16(buf, 16), code = BitConverter.ToUInt16(buf, 18);
                    int value = BitConverter.ToInt32(buf, 20);
                    if (type != 1 /* EV_KEY */ || value == 2 /* auto-repeat */) continue;
                    OnKey(EvdevToVk(code), value == 1, value == 0);
                }
            }
            catch { /* unplugged, or access removed */ }
            finally { lock (_devLock) _readers.Remove(path); }
        }) { IsBackground = true, Name = "Ember.Keyboard " + System.IO.Path.GetFileName(path) }.Start();
    }

    /// <summary>
    /// The one-time permission: adds the udev rule through pkexec (asks for the password) and applies
    /// it. Returns null on success, else why it didn't work ("cancelled", "no pkexec", …).
    /// </summary>
    public static async System.Threading.Tasks.Task<string?> EnableEvdevAsync()
    {
        var script = $"printf '%s\\n' '{UdevRule}' > {RulePath} && udevadm control --reload-rules && udevadm trigger --subsystem-match=input --action=change && udevadm settle --timeout=5";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("pkexec") { UseShellExecute = false, RedirectStandardError = true };
            psi.ArgumentList.Add("/bin/sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode switch
            {
                0 => null,
                126 => "cancelled",
                127 => "not authorised",
                _ => err.Trim() is { Length: > 0 } e ? e : $"failed (code {p.ExitCode})",
            };
        }
        catch (System.ComponentModel.Win32Exception) { return "no pkexec"; }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Linux key code (input-event-codes.h) → Windows virtual-key code. Physical keys, US positions.</summary>
    public static int EvdevToVk(int code)
    {
        const string row1 = "QWERTYUIOP", row2 = "ASDFGHJKL", row3 = "ZXCVBNM";
        if (code is >= 16 and <= 25) return row1[code - 16];
        if (code is >= 30 and <= 38) return row2[code - 30];
        if (code is >= 44 and <= 50) return row3[code - 44];
        if (code is >= 2 and <= 10) return 0x31 + (code - 2); // 1..9
        if (code is >= 59 and <= 68) return 0x70 + (code - 59); // F1..F10
        if (code is >= 183 and <= 194) return 0x7C + (code - 183); // F13..F24
        return code switch
        {
            1 => 0x1B, 11 => 0x30, 12 => 0xBD, 13 => 0xBB, 14 => 0x08, 15 => 0x09, 26 => 0xDB, 27 => 0xDD, 28 => 0x0D,
            29 => 0xA2, 39 => 0xBA, 40 => 0xDE, 41 => 0xC0, 42 => 0xA0, 43 => 0xDC, 51 => 0xBC, 52 => 0xBE, 53 => 0xBF,
            54 => 0xA1, 55 => 0x6A, 56 => 0xA4, 57 => 0x20, 58 => 0x14, 69 => 0x90, 70 => 0x91,
            71 => 0x67, 72 => 0x68, 73 => 0x69, 74 => 0x6D, 75 => 0x64, 76 => 0x65, 77 => 0x66, 78 => 0x6B,
            79 => 0x61, 80 => 0x62, 81 => 0x63, 82 => 0x60, 83 => 0x6E, 87 => 0x7A, 88 => 0x7B, 96 => 0x0D,
            97 => 0xA3, 98 => 0x6F, 99 => 0x2C, 100 => 0xA5, 102 => 0x24, 103 => 0x26, 104 => 0x21, 105 => 0x25,
            106 => 0x27, 107 => 0x23, 108 => 0x28, 109 => 0x22, 110 => 0x2D, 111 => 0x2E, 119 => 0x13,
            125 => 0x5B, 126 => 0x5C, 127 => 0x5D,
            _ => 0x20000 + code,
        };
    }

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
        0x13 => "Pause", 0x91 => "Scroll Lock", 0x2C => "Print Screen", 0x14 => "Caps Lock", 0x90 => "Num Lock",
        0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]", 0xBA => ";", 0xDE => "'", 0xC0 => "`", 0xDC => "\\",
        0xBC => ",", 0xBE => ".", 0xBF => "/", 0x6A => "Num *", 0x6D => "Num -", 0x6B => "Num +", 0x6E => "Num .", 0x6F => "Num /",
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
