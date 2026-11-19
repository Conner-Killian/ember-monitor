using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Ember.Models;
using Ember.Services.Linux;
using Ember.ViewModels;
using Ember.Views;

namespace Ember.Services;

/// <summary>
/// Diagnostics: `ember-monitor --snapshot DIR` writes sensors.txt (everything detected, plus setup
/// hints) and renders every page and overlay style to PNG, then exits. Useful for bug reports.
/// </summary>
public static class SnapshotTool
{
    public static async Task Run(string dir, MainWindow main)
    {
        Directory.CreateDirectory(dir);
        for (int i = 0; i < 80 && App.Sensors.Latest == null; i++) await Task.Delay(250);
        await Task.Delay(4000);
        var snap = App.Sensors.Latest;
        var sb = new StringBuilder();
        sb.AppendLine($"Ember {typeof(App).Assembly.GetName().Version} · {Desktop.Name} · {Desktop.SessionType} session · demo={App.Sensors.IsDemo}");
        sb.AppendLine($"Kernel: {SysFs.ReadText("/proc/sys/kernel/osrelease")?.Trim()}  Hotkey listener: {(App.Hotkey.IsActive ? "XInput2" : "none (Wayland)")}");
        var h = App.Sensors.Hints;
        sb.AppendLine($"Hints: cpuTemp={!h.NoCpuTemperature} nvml={!h.NvidiaWithoutNvml} drivetemp={!h.SataWithoutDrivetemp} rapl={!h.CpuPowerDenied} dimm={!h.NoDimmSensors} board={!h.NoBoardSensors}");
        if (snap == null) { sb.AppendLine("No sensor snapshot: " + App.Sensors.FailureMessage); File.WriteAllText(Path.Combine(dir, "sensors.txt"), sb.ToString()); return; }
        sb.AppendLine($"CPU: {snap.Cpu?.Name} temp={snap.Cpu?.PrimaryTemp?.Name}={snap.CpuTemp:0.0} load={snap.Cpu?.Load?.Value:0.0} clock={snap.Cpu?.Clock?.Value:0} power={snap.Cpu?.Power?.Value:0.0}");
        sb.AppendLine($"GPU: {snap.Gpu?.Name} temp={snap.GpuTemp:0.0} load={snap.Gpu?.Load?.Value:0.0} power={snap.Gpu?.Power?.Value:0.0} integrated={snap.Gpu?.IsIntegratedGpu}");
        sb.AppendLine($"RAM: {snap.Memory?.Load?.Value:0.0}%  DIMMs: {string.Join(", ", snap.RamTemps.Select(r => $"{r.Name}={r.Value}"))}");
        foreach (var d in snap.Drives) sb.AppendLine($"Drive: {d.Name} [{d.Media}] {d.PrimaryTemp?.Name}={d.PrimaryTemp?.Value} used={d.Load?.Value:0}%");
        sb.AppendLine("\nUpdate cost (ms, rolling avg):");
        foreach (var kv in App.Sensors.UpdateCostMs.OrderByDescending(k => k.Value)) sb.AppendLine($"   {kv.Value,8:0.00}  {kv.Key}");
        sb.AppendLine();
        foreach (var d in snap.Devices)
        {
            sb.AppendLine($"== {d.Name} [{d.HardwareType} / {d.Kind}] id={d.Id}");
            foreach (var s in d.Sensors) sb.AppendLine($"   {s.Type,-12} {s.Name,-36} {s.Value}");
        }
        File.WriteAllText(Path.Combine(dir, "sensors.txt"), sb.ToString());

        // Pages
        main.Width = 1280; main.Height = 900;
        foreach (var nav in new[] { "NavDashboard", "NavSensors", "NavOverlay", "NavSettings" })
        {
            main.Navigate(nav);
            main.UpdateSnapshot(snap);
            await Task.Delay(1200);
            Render(main, Path.Combine(dir, $"main_{nav[3..].ToLowerInvariant()}.png"));
        }
        main.Navigate("NavDashboard");

        // Overlay styles: the real overlay window (styles only apply to controls inside a window).
        var settings = App.Settings;
        var saved = (settings.OverlayLayout, settings.OverlayShowRam, settings.OverlayShowDrives, settings.OverlayShowUsage, settings.OverlayShowPower, settings.OverlayVisible);
        if (App.Current.Overlay is { } ov)
        {
            settings.OverlayVisible = true;
            foreach (var layout in Enum.GetValues<OverlayLayout>())
            {
                settings.OverlayLayout = layout;
                settings.OverlayShowRam = settings.OverlayShowDrives = settings.OverlayShowUsage = settings.OverlayShowPower = true;
                App.NotifySettingsChanged();
                await Task.Delay(700);
                Render(ov, Path.Combine(dir, $"overlay_{layout.ToString().ToLowerInvariant()}.png"));
            }
        }
        (settings.OverlayLayout, settings.OverlayShowRam, settings.OverlayShowDrives, settings.OverlayShowUsage, settings.OverlayShowPower, settings.OverlayVisible) = saved;
        App.NotifySettingsChanged();

        using (var icon = App.RenderTempIcon("51", Severity.Normal, 64)) icon.Save(Path.Combine(dir, "tray_51.png"));
    }

    private static void Render(Visual v, string path)
    {
        var size = v.Bounds.Size;
        if (size.Width < 1 || size.Height < 1) return;
        using var rtb = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)));
        rtb.Render(v);
        rtb.Save(path);
    }
}
