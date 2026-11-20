using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Ember.Services;

/// <summary>
/// Global hotkey via a low-level keyboard hook. Unlike RegisterHotKey this supports
/// side-specific modifiers (Right Ctrl vs Left Ctrl) and modifier-only combos.
///
/// Rules:
///  • Combo with a normal key  → fires on key-down of the final key while exactly the combo is held.
///  • Modifier-only combo      → fires on release, only if no other key was pressed during the chord
///                                (so Ctrl+Alt+Del or RCtrl+RAlt+X never toggle the overlay).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    private const int LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public int vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern int GetKeyNameText(int lParam, StringBuilder buf, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    private const uint WM_QUIT = 0x12;

    private readonly LowLevelKeyboardProc _proc;
    private readonly System.Windows.Threading.Dispatcher _ui;
    private readonly object _lock = new();
    private IntPtr _hook;
    private System.Threading.Thread? _thread;
    private uint _threadId;
    private readonly HashSet<int> _down = new();
    private List<Binding> _bindings = new();

    /// <summary>One hotkey: the main show / hide key (id "") or a preset's (its id).</summary>
    private sealed class Binding
    {
        public required string Id { get; init; }
        public required HashSet<int> Combo { get; init; }
        public bool Dirty;  // another key was pressed during a modifier-only chord
        public bool Armed;  // combo fully held at some point
        public bool Fired;
        public bool ModifierOnly => Combo.All(IsModifier);
        public void Reset() => Dirty = Armed = Fired = false;
    }

    /// <summary>Raised on the hook thread with the hotkey's id ("" = show / hide, else a preset id); subscribers marshal to the UI themselves.</summary>
    public event Action<string>? Pressed;

    /// <summary>
    /// When set, keystrokes are swallowed and routed here instead of triggering (the "record hotkey"
    /// UI). Called on the UI thread.
    /// </summary>
    public Action<int, bool>? Recorder { get; set; }

    public HotkeyService()
    {
        _proc = HookProc;
        _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    }

    /// <summary>
    /// The hook lives on its own thread with its own message loop. Every keystroke on the PC passes
    /// through a low-level hook, so if it shared the UI thread any UI stall would delay typing
    /// system-wide, and after ~1 s Windows silently removes the hook (the hotkey just stops working).
    /// </summary>
    public void Install()
    {
        if (_thread != null) return;
        using var ready = new System.Threading.ManualResetEventSlim();
        _thread = new System.Threading.Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
            ready.Set();
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        })
        { IsBackground = true, Name = "Ember.Hotkey", Priority = System.Threading.ThreadPriority.AboveNormal };
        _thread.Start();
        ready.Wait(3000);
    }

    /// <summary>The hotkeys to watch: id → key combination ("" = show / hide overlay, else a preset id). Empty combos are skipped.</summary>
    public void SetBindings(IEnumerable<(string id, IEnumerable<int> combo)> bindings)
    {
        var list = bindings.Select(b => new Binding { Id = b.id, Combo = new HashSet<int>(b.combo) })
                           .Where(b => b.Combo.Count > 0).ToList();
        lock (_lock)
        {
            // Unchanged? Keep the chord state (settings are re-applied on every change).
            if (list.Count == _bindings.Count && list.Zip(_bindings).All(z => z.First.Id == z.Second.Id && z.First.Combo.SetEquals(z.Second.Combo))) return;
            _bindings = list;
            _down.Clear();
        }
    }

    public static bool IsModifier(int vk) => vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C
                                                 or 0x10 or 0x11 or 0x12;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = info.vkCode;
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;

            // On some layouts Right Alt (AltGr) also synthesises a fake Left Ctrl – ignore it.
            bool fakeCtrl = vk == 0xA2 && (info.scanCode & 0x200) != 0;

            if (!fakeCtrl && (info.flags & LLKHF_INJECTED) == 0)
            {
                var rec = Recorder;
                if (rec != null)
                {
                    // Swallow keys while recording so e.g. Win doesn't open Start or Space doesn't click buttons.
                    _ui.BeginInvoke(rec, vk, down);
                    return (IntPtr)1;
                }
                string? fired = null;
                lock (_lock)
                {
                    if (_bindings.Count > 0)
                        try { fired = Handle(vk, down, up); } catch { }
                }
                if (fired != null) Pressed?.Invoke(fired);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Tracks the chord; returns the id of the hotkey that should fire, if any.</summary>
    private string? Handle(int vk, bool down, bool up)
    {
        string? fired = null;
        if (down)
        {
            // A key released while another desktop had input (lock screen, UAC prompt, Ctrl+Alt+Del)
            // never sends us its key-up. Drop keys that aren't physically held any more, otherwise
            // one stale key would block the hotkey until Ember restarts.
            _down.RemoveWhere(k => k != vk && (GetAsyncKeyState(k) & 0x8000) == 0);
            if (_down.Count == 0) foreach (var b in _bindings) b.Reset();

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

    // ---------------- Display helpers ----------------

    public static string Describe(IEnumerable<int> vks)
    {
        var list = vks.OrderBy(v => IsModifier(v) ? 0 : 1).ThenBy(v => v).ToList();
        return list.Count == 0 ? "Not set" : string.Join(" + ", list.Select(KeyName));
    }

    public static string KeyName(int vk) => vk switch
    {
        0xA0 => "Left Shift", 0xA1 => "Right Shift",
        0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl",
        0xA4 => "Left Alt", 0xA5 => "Right Alt",
        0x5B => "Left Win", 0x5C => "Right Win",
        0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc", 0x09 => "Tab", 0x08 => "Backspace",
        0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End", 0x21 => "Page Up", 0x22 => "Page Down",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0x13 => "Pause", 0x91 => "Scroll Lock", 0x2C => "Print Screen",
        >= 0x60 and <= 0x69 => "Num " + (vk - 0x60),
        _ => FromSystem(vk),
    };

    private static string FromSystem(int vk)
    {
        uint sc = MapVirtualKey((uint)vk, 0);
        var sb = new StringBuilder(64);
        if (sc != 0 && GetKeyNameText((int)(sc << 16), sb, 64) > 0) return sb.ToString();
        return $"Key {vk:X2}";
    }

    public void Dispose()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }
}
