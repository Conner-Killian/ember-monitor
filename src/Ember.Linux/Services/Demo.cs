using System;
using System.Collections.Generic;

namespace Ember.Services;

/// <summary>
/// Plausible, slowly drifting readings for running the UI where real sensors aren't available
/// (development on another OS, screenshots, EMBER_DEMO=1).
/// </summary>
internal static class Demo
{
    private static double Wave(long t, double period, double phase = 0) => Math.Sin((t / period + phase) * Math.PI * 2);

    public static List<DeviceSnapshot> Devices(long t)
    {
        var list = new List<DeviceSnapshot>();
        double cpuT = 54 + 6 * Wave(t, 40) + 2 * Wave(t, 7, 0.3);
        double cpuLoad = Math.Clamp(18 + 14 * Wave(t, 25) + 6 * Wave(t, 5), 1, 100);
        var cpuS = new List<SensorReading>
        {
            new("/cpu/k10temp0/temp1", "Core (Tctl/Tdie)", "Temperature", cpuT, null, null),
            new("/cpu/k10temp0/temp3", "CCD1 (Tdie)", "Temperature", cpuT - 9, null, null),
            new("/cpu/k10temp0/temp4", "CCD2 (Tdie)", "Temperature", cpuT - 7, null, null),
            new("/cpu/ccdmax", "CCDs Max (Tdie)", "Temperature", cpuT - 7, null, null),
            new("/cpu/usage", "CPU Usage", "Load", cpuLoad, null, null),
            new("/cpu/speed", "CPU Speed", "Clock", 4600 + 500 * Wave(t, 9), null, null),
            new("/cpu/power", "Package", "Power", 60 + cpuLoad, null, null),
        };
        for (int i = 0; i < 8; i++)
            cpuS.Add(new($"/cpu/load/cpu{i}", $"CPU Core #{i + 1}", "Load", Math.Clamp(cpuLoad + 10 * Wave(t, 6, i * 0.13), 0, 100), null, null));
        list.Add(new DeviceSnapshot("/cpu/0", "AMD Ryzen 9 7950X", DeviceKind.Cpu, "Cpu", cpuS)
        { PrimaryTemp = cpuS[0], SecondaryTemp = cpuS[3], Load = cpuS[4], Clock = cpuS[5], Power = cpuS[6] });

        var ramLoad = new SensorReading("/ram/load", "Memory", "Load", 41 + 3 * Wave(t, 60), null, null);
        list.Add(new DeviceSnapshot("/ram", "Total Memory", DeviceKind.Memory, "Memory", new List<SensorReading>
        {
            ramLoad,
            new("/ram/used", "Memory Used", "Data", 12.9 + Wave(t, 60), null, null),
            new("/ram/avail", "Memory Available", "Data", 18.3 - Wave(t, 60), null, null),
        }) { Load = ramLoad });
        for (int i = 1; i <= 2; i++)
        {
            var dimm = new SensorReading($"/memory/dimm/{i}/temp1", $"DIMM #{i}", "Temperature", 44 + i + Wave(t, 80), null, null);
            list.Add(new DeviceSnapshot($"/memory/dimm/{i}", $"KF560C36-16 (#{i})", DeviceKind.Memory, "Memory", new List<SensorReading> { dimm }) { PrimaryTemp = dimm });
        }

        double gpuLoad = Math.Clamp(30 + 25 * Wave(t, 30, 0.2), 0, 100);
        var gpuS = new List<SensorReading>
        {
            new("/gpu/0000:03:00.0/Temperature/GPU Core", "GPU Core", "Temperature", 50 + gpuLoad / 5, null, null),
            new("/gpu/0000:03:00.0/Temperature/GPU Hot Spot", "GPU Hot Spot", "Temperature", 62 + gpuLoad / 4, null, null),
            new("/gpu/0000:03:00.0/Load/GPU Core", "GPU Core", "Load", gpuLoad, null, null),
            new("/gpu/0000:03:00.0/Power/GPU Package", "GPU Package", "Power", 60 + gpuLoad * 2.2, null, null),
            new("/gpu/0000:03:00.0/Clock/GPU Core", "GPU Core", "Clock", 1800 + gpuLoad * 8, null, null),
            new("/gpu/0000:03:00.0/temp/fan1", "GPU Fan", "Fan", gpuLoad > 25 ? 900 + gpuLoad * 12 : 0, null, null),
        };
        list.Add(new DeviceSnapshot("/gpu/0000:03:00.0", "AMD Radeon RX 7900 XTX", DeviceKind.Gpu, "GpuAmd", gpuS)
        { PrimaryTemp = gpuS[0], SecondaryTemp = gpuS[1], Load = gpuS[2], Power = gpuS[3], Clock = gpuS[4], Fan = gpuS[5] });

        void Drive(string id, string name, double temp, double used, DriveMedia media)
        {
            var tr = new SensorReading(id + "/temp1", media == DriveMedia.Hdd ? "Temperature" : "Composite Temperature", "Temperature", temp + Wave(t, 90), null, null);
            var ur = new SensorReading(id + "/used", "Used Space", "Load", used, null, null);
            list.Add(new DeviceSnapshot(id, name, DeviceKind.Storage, "Storage", new List<SensorReading> { tr, ur }) { PrimaryTemp = tr, Load = ur, Media = media });
        }
        Drive("/nvme/S6B0NL0W", "Samsung SSD 990 PRO 2TB", 41, 38, DriveMedia.Ssd);
        Drive("/ata/ST4000DM004/sda", "ST4000DM004-2CV104", 37, 71, DriveMedia.Hdd);

        var board = new List<SensorReading>
        {
            new("/hwmon/nct6798/1/temp1", "SYSTIN", "Temperature", 36 + Wave(t, 120), null, null),
            new("/hwmon/nct6798/1/temp2", "CPUTIN", "Temperature", 45 + Wave(t, 50), null, null),
            new("/hwmon/nct6798/1/fan1", "Fan #1", "Fan", 820 + 30 * Wave(t, 20), null, null),
            new("/hwmon/nct6798/1/fan2", "Fan #2", "Fan", 1240 + 60 * Wave(t, 20), null, null),
        };
        list.Add(new DeviceSnapshot("/hwmon/nct6798/1", "Nuvoton NCT6798", DeviceKind.Motherboard, "SuperIO", board) { PrimaryTemp = board[0] });
        return list;
    }
}
