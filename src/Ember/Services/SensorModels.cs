using System;
using System.Collections.Generic;

namespace Ember.Services;

public enum DeviceKind { Cpu, Gpu, Memory, Motherboard, Storage, Other }
public enum DriveMedia { Ssd, Hdd }

public sealed record SensorReading(
    string Id,
    string Name,
    string Type,          // LHM SensorType as string (Temperature, Load, Power, Fan, Clock, ...)
    double? Value,
    double? Min,
    double? Max);

public sealed record DeviceSnapshot(
    string Id,
    string Name,
    DeviceKind Kind,
    string HardwareType,
    IReadOnlyList<SensorReading> Sensors)
{
    /// <summary>Primary temperature sensor for this device (null if none).</summary>
    public SensorReading? PrimaryTemp { get; init; }
    public SensorReading? SecondaryTemp { get; init; }   // e.g. GPU hot spot, CPU CCD
    public SensorReading? Load { get; init; }
    public SensorReading? Power { get; init; }
    public SensorReading? Clock { get; init; }
    public SensorReading? Fan { get; init; }
    public DriveMedia Media { get; init; } = DriveMedia.Ssd;
    public bool IsIntegratedGpu { get; init; }
}

public sealed class SensorSnapshot
{
    public DateTime Time { get; init; } = DateTime.Now;
    public IReadOnlyList<DeviceSnapshot> Devices { get; init; } = Array.Empty<DeviceSnapshot>();

    public DeviceSnapshot? Cpu { get; init; }
    public DeviceSnapshot? Gpu { get; init; }            // primary GPU
    public IReadOnlyList<DeviceSnapshot> Gpus { get; init; } = Array.Empty<DeviceSnapshot>();
    public IReadOnlyList<SensorReading> RamTemps { get; init; } = Array.Empty<SensorReading>();
    public IReadOnlyList<DeviceSnapshot> Drives { get; init; } = Array.Empty<DeviceSnapshot>();
    public DeviceSnapshot? Memory { get; init; }
    public DeviceSnapshot? Motherboard { get; init; }

    public double? CpuTemp => Cpu?.PrimaryTemp?.Value;
    public double? GpuTemp => Gpu?.PrimaryTemp?.Value;
    public double? RamTemp
    {
        get
        {
            double? max = null;
            foreach (var r in RamTemps)
                if (r.Value is double v && (max == null || v > max)) max = v;
            return max;
        }
    }
}

/// <summary>Fixed-size ring buffer of samples for sparkline history.</summary>
public sealed class History
{
    private readonly double[] _buf;
    private int _start, _count;
    public History(int capacity) => _buf = new double[capacity];
    public int Count => _count;
    public int Capacity => _buf.Length;

    public void Add(double v)
    {
        if (_count < _buf.Length) { _buf[(_start + _count) % _buf.Length] = v; _count++; }
        else { _buf[_start] = v; _start = (_start + 1) % _buf.Length; }
    }

    public double[] ToArray()
    {
        var a = new double[_count];
        for (int i = 0; i < _count; i++) a[i] = _buf[(_start + i) % _buf.Length];
        return a;
    }
}
