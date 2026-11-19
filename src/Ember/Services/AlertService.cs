using System;
using System.Collections.Generic;

namespace Ember.Services;

/// <summary>Raises a tray notification when a component stays above its critical threshold.</summary>
public sealed class AlertService
{
    private readonly Dictionary<string, DateTime> _hotSince = new();
    private readonly Dictionary<string, DateTime> _lastNotified = new();
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    public event Action<string, string>? Notify; // title, message

    public void Evaluate(SensorSnapshot snap)
    {
        var s = App.Settings;
        if (!s.AlertsNotify) { _hotSince.Clear(); return; }

        if (snap.Cpu != null) Check("cpu", "CPU", snap.CpuTemp, Temps.For(DeviceKind.Cpu));
        if (snap.Gpu != null) Check("gpu", "GPU", snap.GpuTemp, Temps.For(DeviceKind.Gpu));
        if (snap.RamTemps.Count > 0) Check("ram", "Memory", snap.RamTemp, Temps.For(DeviceKind.Memory));
        foreach (var d in snap.Drives)
            Check("drive:" + d.Id, d.Name, d.PrimaryTemp?.Value, Temps.For(DeviceKind.Storage, d.Media));
    }

    private void Check(string key, string label, double? temp, Models.Thresholds t)
    {
        var now = DateTime.Now;
        if (temp is double v && v >= t.Critical)
        {
            if (!_hotSince.TryGetValue(key, out var since)) { _hotSince[key] = now; return; }
            if ((now - since).TotalSeconds < App.Settings.AlertSustainSeconds) return;
            if (_lastNotified.TryGetValue(key, out var last) && now - last < Cooldown) return;
            _lastNotified[key] = now;
            Notify?.Invoke($"{label} is running hot",
                $"{label} has been at {Temps.Format(v)} for over {App.Settings.AlertSustainSeconds}s " +
                $"(critical threshold {Temps.Format(t.Critical)}).");
        }
        else
        {
            _hotSince.Remove(key);
        }
    }
}
