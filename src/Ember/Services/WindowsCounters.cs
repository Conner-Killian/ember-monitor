using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ember.Services;

/// <summary>
/// Reads the Windows performance counters Task Manager uses, so Ember's headline CPU numbers match it.
/// LibreHardwareMonitor's "CPU Total" is plain busy time; Task Manager (Windows 10 2004+/11) shows
/// "% Processor Utility", which also accounts for boost clocks and reads noticeably higher.
/// </summary>
internal sealed class WindowsCounters : IDisposable
{
    private IntPtr _query;
    private readonly IntPtr _utility, _performance, _frequency;

    public WindowsCounters()
    {
        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
            _utility = Add(@"\Processor Information(_Total)\% Processor Utility");
            _performance = Add(@"\Processor Information(_Total)\% Processor Performance");
            _frequency = Add(@"\Processor Information(_Total)\Processor Frequency");
        }
        catch { _query = IntPtr.Zero; }
    }

    private IntPtr Add(string path) =>
        PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero;

    /// <summary>Takes a sample. Rate counters need two samples before they return a value.</summary>
    public void Collect()
    {
        if (_query != IntPtr.Zero) PdhCollectQueryData(_query);
    }

    /// <summary>CPU usage as Task Manager shows it (0–100), or null if unavailable.</summary>
    public double? CpuUsage => Read(_utility) is double v ? Math.Clamp(v, 0, 100) : null;

    /// <summary>CPU speed in MHz as Task Manager shows it (base frequency × % performance).</summary>
    public double? CpuSpeedMhz =>
        Read(_performance) is double p && Read(_frequency) is double f && f > 0 && p > 0 ? f * p / 100 : null;

    private static double? Read(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return null;
        try
        {
            if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) != 0) return null;
            if (v.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)) return null;
            return double.IsFinite(v.Value) ? v.Value : null;
        }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
    }

    // ---- diagnostics: Task Manager's GPU figure computed independently of LibreHardwareMonitor ----

    /// <summary>
    /// Task Manager's per-GPU utilisation: for every engine, add up all processes' share, then take
    /// the busiest engine. Keyed by adapter LUID. Expensive (one instance per process per engine),
    /// so only used by the --snapshot comparison, never during normal polling.
    /// </summary>
    public sealed class GpuEngineSampler : IDisposable
    {
        private IntPtr _q, _c;
        public GpuEngineSampler()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) { _q = IntPtr.Zero; return; }
            if (PdhAddEnglishCounterW(_q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _c) != 0) _c = IntPtr.Zero;
            PdhCollectQueryData(_q);
        }

        public Dictionary<string, double> Sample()
        {
            var perGpu = new Dictionary<string, double>();
            if (_q == IntPtr.Zero || _c == IntPtr.Zero) return perGpu;
            PdhCollectQueryData(_q);
            uint size = 0;
            PdhGetFormattedCounterArrayW(_c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, IntPtr.Zero);
            if (size == 0) return perGpu;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(_c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return perGpu;
                var perEngine = new Dictionary<string, double>();
                int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * itemSize);
                    var name = Marshal.PtrToStringUni(item.szName) ?? "";
                    int at = name.IndexOf("luid_", StringComparison.Ordinal);
                    if (at < 0 || item.Value.CStatus > PDH_CSTATUS_NEW_DATA) continue;
                    var engine = name[at..]; // luid_..._phys_N_eng_M_engtype_X
                    perEngine[engine] = (perEngine.TryGetValue(engine, out var e) ? e : 0) + item.Value.Value;
                }
                foreach (var (engine, v) in perEngine)
                {
                    int phys = engine.IndexOf("_phys_", StringComparison.Ordinal);
                    var luid = phys > 0 ? engine[..phys] : engine;
                    perGpu[luid] = Math.Max(perGpu.TryGetValue(luid, out var g) ? g : 0, Math.Min(100, v));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return perGpu;
        }

        public void Dispose() { if (_q != IntPtr.Zero) { PdhCloseQuery(_q); _q = IntPtr.Zero; } }
    }

    // ---- PDH interop ----
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_CSTATUS_VALID_DATA = 0;
    private const uint PDH_CSTATUS_NEW_DATA = 1;

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);
    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
