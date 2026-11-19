using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Ember.Services.Linux;

/// <summary>
/// NVIDIA Management Library (libnvidia-ml.so.1, installed with the proprietary NVIDIA driver).
/// The proprietary driver exposes no hwmon sensors, so this is the only source of NVIDIA readings.
/// Loaded dynamically: Ember runs fine on machines without it.
/// </summary>
public sealed unsafe class Nvml : IDisposable
{
    private readonly IntPtr _lib;
    private readonly delegate* unmanaged[Cdecl]<int> _shutdown;
    private readonly delegate* unmanaged[Cdecl]<uint*, int> _getCount;
    private readonly delegate* unmanaged[Cdecl]<uint, IntPtr*, int> _getHandle;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int> _getName;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _getTemp;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int> _getUtil;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, int> _getPower;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int> _getClock;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, int> _getFan;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, MemoryInfo*, int> _getMem;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, PciInfo*, int> _getPci;

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain, Bus, Device, PciDeviceId, PciSubSystemId;
        public fixed byte BusId[32];
    }

    public sealed record Gpu(IntPtr Handle, string Name, string PciSlot);
    public List<Gpu> Gpus { get; } = new();

    private Nvml(IntPtr lib)
    {
        _lib = lib;
        IntPtr E(string name) => NativeLibrary.GetExport(lib, name);
        var init = (delegate* unmanaged[Cdecl]<int>)E("nvmlInit_v2");
        if (init() != 0) throw new InvalidOperationException("nvmlInit failed");
        _shutdown = (delegate* unmanaged[Cdecl]<int>)E("nvmlShutdown");
        _getCount = (delegate* unmanaged[Cdecl]<uint*, int>)E("nvmlDeviceGetCount_v2");
        _getHandle = (delegate* unmanaged[Cdecl]<uint, IntPtr*, int>)E("nvmlDeviceGetHandleByIndex_v2");
        _getName = (delegate* unmanaged[Cdecl]<IntPtr, byte*, uint, int>)E("nvmlDeviceGetName");
        _getTemp = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)E("nvmlDeviceGetTemperature");
        _getUtil = (delegate* unmanaged[Cdecl]<IntPtr, Utilization*, int>)E("nvmlDeviceGetUtilizationRates");
        _getPower = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)E("nvmlDeviceGetPowerUsage");
        _getClock = (delegate* unmanaged[Cdecl]<IntPtr, int, uint*, int>)E("nvmlDeviceGetClockInfo");
        _getFan = (delegate* unmanaged[Cdecl]<IntPtr, uint*, int>)E("nvmlDeviceGetFanSpeed");
        _getMem = (delegate* unmanaged[Cdecl]<IntPtr, MemoryInfo*, int>)E("nvmlDeviceGetMemoryInfo");
        _getPci = (delegate* unmanaged[Cdecl]<IntPtr, PciInfo*, int>)E("nvmlDeviceGetPciInfo_v3");

        uint count = 0;
        var nameBuf = stackalloc byte[96];
        if (_getCount(&count) != 0) return;
        for (uint i = 0; i < count; i++)
        {
            IntPtr h;
            if (_getHandle(i, &h) != 0) continue;
            string name = _getName(h, nameBuf, 96) == 0 ? Encoding.UTF8.GetString(nameBuf, Strlen(nameBuf, 96)) : "NVIDIA GPU";
            PciInfo pci;
            string slot = _getPci(h, &pci) == 0 ? $"{pci.Domain:x4}:{pci.Bus:x2}:{pci.Device:x2}.0" : $"nvml{i}";
            Gpus.Add(new Gpu(h, name, slot));
        }
    }

    private static int Strlen(byte* p, int max) { int n = 0; while (n < max && p[n] != 0) n++; return n; }

    /// <summary>Loads NVML if the NVIDIA driver is installed; null otherwise.</summary>
    public static Nvml? TryLoad()
    {
        if (!OperatingSystem.IsLinux()) return null;
        foreach (var name in new[] { "libnvidia-ml.so.1", "libnvidia-ml.so" })
        {
            if (!NativeLibrary.TryLoad(name, out var lib)) continue;
            try { return new Nvml(lib); }
            catch { NativeLibrary.Free(lib); }
        }
        return null;
    }

    public double? Temperature(IntPtr h) { uint v; return _getTemp(h, 0 /* NVML_TEMPERATURE_GPU */, &v) == 0 ? v : null; }
    public double? Load(IntPtr h) { Utilization u; return _getUtil(h, &u) == 0 ? u.Gpu : null; }
    public double? MemoryControllerLoad(IntPtr h) { Utilization u; return _getUtil(h, &u) == 0 ? u.Memory : null; }
    public double? PowerWatts(IntPtr h) { uint v; return _getPower(h, &v) == 0 ? v / 1000.0 : null; }
    public double? CoreClockMHz(IntPtr h) { uint v; return _getClock(h, 0 /* GRAPHICS */, &v) == 0 ? v : null; }
    public double? MemoryClockMHz(IntPtr h) { uint v; return _getClock(h, 2 /* MEM */, &v) == 0 ? v : null; }
    public double? FanPercent(IntPtr h) { uint v; return _getFan(h, &v) == 0 ? v : null; }
    public (double usedMb, double totalMb)? Memory(IntPtr h)
    {
        MemoryInfo m;
        return _getMem(h, &m) == 0 ? (m.Used / 1048576.0, m.Total / 1048576.0) : null;
    }

    public void Dispose()
    {
        try { _shutdown(); } catch { }
        NativeLibrary.Free(_lib);
    }
}
