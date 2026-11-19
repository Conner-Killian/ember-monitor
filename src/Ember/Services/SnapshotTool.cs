using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ember.Models;
using Ember.ViewModels;
using Ember.Views;

namespace Ember.Services;

/// <summary>
/// Diagnostics: `Ember.exe --snapshot <dir>` renders every page and overlay style to PNG and
/// writes a full sensor dump, then exits. Useful for bug reports.
/// </summary>
public static class SnapshotTool
{
    /// <summary>
    /// Samples Ember's headline numbers next to Task Manager's figures computed independently
    /// (own PDH queries, GlobalMemoryStatusEx, per-process GPU engine counters), so any metric that
    /// drifts from what Windows reports shows up in the dump. Vendor/LHM values listed for contrast.
    /// </summary>
    private static async Task CompareWithWindows(StringBuilder sb)
    {
        using var win = new WindowsCounters();
        using var gpuEngines = new WindowsCounters.GpuEngineSampler();
        win.Collect();
        sb.AppendLine("Compare with Windows / Task Manager (Ember | Windows | other source for contrast):");
        sb.AppendLine("   CPU usage %          CPU speed GHz      RAM %          GPU usage %");
        double cpuDiff = 0, spdDiff = 0, ramDiff = 0, gpuDiff = 0; int n = 0;
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(1000);
            win.Collect();
            var gpuWin = gpuEngines.Sample();
            var s = App.Sensors.Latest;
            if (s == null) continue;
            double? eCpu = s.Cpu?.Load?.Value, wCpu = win.CpuUsage;
            double? lhmCpu = s.Cpu?.Sensors.FirstOrDefault(x => x.Type == "Load" && x.Name == "CPU Total")?.Value;
            double? eSpd = s.Cpu?.Clock?.Value / 1000, wSpd = win.CpuSpeedMhz / 1000;
            double? eRam = s.Memory?.Load?.Value;
            var ms = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
            double? wRam = GlobalMemoryStatusEx(ref ms) ? 100.0 * (ms.ullTotalPhys - ms.ullAvailPhys) / ms.ullTotalPhys : null;
            double? eGpu = s.Gpu?.Load?.Value;
            double? wGpu = gpuWin.Count > 0 ? gpuWin.Values.Max() : null;
            double? vGpu = s.Gpu?.Sensors.FirstOrDefault(x => x.Type == "Load" && x.Name == "GPU Core")?.Value;
            string F(double? v, string f = "0.0") => v is double d ? d.ToString(f).PadLeft(5) : "    —";
            sb.AppendLine($"   {F(eCpu)} |{F(wCpu)} |{F(lhmCpu)}   {F(eSpd, "0.00")} |{F(wSpd, "0.00")}   {F(eRam)} |{F(wRam)}   {F(eGpu)} |{F(wGpu)} |{F(vGpu)}");
            if (eCpu is double a && wCpu is double b) cpuDiff = Math.Max(cpuDiff, Math.Abs(a - b));
            if (eSpd is double c && wSpd is double e) spdDiff = Math.Max(spdDiff, Math.Abs(c - e));
            if (eRam is double f2 && wRam is double g) ramDiff = Math.Max(ramDiff, Math.Abs(f2 - g));
            if (eGpu is double h && wGpu is double k) gpuDiff = Math.Max(gpuDiff, Math.Abs(h - k));
            n++;
        }
        sb.AppendLine($"   largest gap over {n} samples: CPU {cpuDiff:0.0} pts, speed {spdDiff:0.00} GHz, RAM {ramDiff:0.0} pts, GPU {gpuDiff:0.0} pts");
        sb.AppendLine("   (Ember polls on its own 1 s clock, so a few points of gap on a changing load is sampling offset.)");
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private static int CountRows(DependencyObject root)
    {
        int n = root is FrameworkElement { DataContext: ViewModels.SensorRowVM } and ContentPresenter ? 1 : 0;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) n += CountRows(VisualTreeHelper.GetChild(root, i));
        return n;
    }

    public static async Task Run(string dir, MainWindow main)
    {
        Directory.CreateDirectory(dir);
        // Wait for a few samples so history graphs have data
        for (int i = 0; i < 80 && (App.Sensors.Latest == null); i++) await Task.Delay(250);
        await Task.Delay(6000);
        var snap = App.Sensors.Latest;
        if (snap == null) { File.WriteAllText(Path.Combine(dir, "error.txt"), "No snapshot"); return; }

        // ---- sensor dump ----
        var sb = new StringBuilder();
        sb.AppendLine($"Driver installed: {App.Sensors.DriverInstalled}");
        sb.AppendLine($"CPU: {snap.Cpu?.Name} primary={snap.Cpu?.PrimaryTemp?.Name}={snap.CpuTemp}");
        sb.AppendLine($"GPU: {snap.Gpu?.Name} primary={snap.Gpu?.PrimaryTemp?.Name}={snap.GpuTemp} integrated={snap.Gpu?.IsIntegratedGpu}");
        sb.AppendLine($"RAM temps: {string.Join(", ", snap.RamTemps.Select(r => $"{r.Name}={r.Value}"))}");
        foreach (var d in snap.Drives) sb.AppendLine($"Drive: {d.Name} media={d.Media} temp={d.PrimaryTemp?.Name}={d.PrimaryTemp?.Value}");
        sb.AppendLine();
        sb.AppendLine("Update cost (ms, rolling avg):");
        foreach (var kv in App.Sensors.UpdateCostMs.OrderByDescending(k => k.Value)) sb.AppendLine($"   {kv.Value,8:0.0}  {kv.Key}");
        // Measure steady-state CPU split over 15 s
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        double p0 = proc.TotalProcessorTime.TotalMilliseconds, s0 = App.Sensors.PollThreadCpuMs, u0 = SensorService.ThreadCpuMs();
        await Task.Delay(15000);
        proc.Refresh();
        double p1 = proc.TotalProcessorTime.TotalMilliseconds, s1 = App.Sensors.PollThreadCpuMs, u1 = SensorService.ThreadCpuMs();
        sb.AppendLine($"CPU over 15s (% of one core): process {(p1 - p0) / 150:0.0}  poll thread {(s1 - s0) / 150:0.0}  UI thread {(u1 - u0) / 150:0.0}");
        foreach (var kv in App.Sensors.UpdateCostMs.OrderByDescending(k => k.Value)) sb.AppendLine($"   {kv.Value,8:0.0}  {kv.Key}");
        sb.AppendLine();
        await CompareWithWindows(sb);
        sb.AppendLine();
        foreach (var d in snap.Devices)
        {
            sb.AppendLine($"== {d.Name} [{d.HardwareType} / {d.Kind}] id={d.Id}");
            foreach (var s in d.Sensors) sb.AppendLine($"   {s.Type,-12} {s.Name,-36} {s.Value}");
        }
        File.WriteAllText(Path.Combine(dir, "sensors.txt"), sb.ToString());

        // ---- live overlay placement after style / content changes ----
        if (App.Current.Overlay is { IsVisible: true } ov)
        {
            var st = App.Settings;
            var keep = (st.OverlayLayout, st.OverlayAnchor, st.OverlayShowUsage, st.OverlayShowPower);
            var place = new StringBuilder("\nPlacement checks (settled 800 ms after each change):\n");
            OverlayWindow.TraceReposition = true;
            st.OverlayAnchor = OverlayAnchor.TopRight;
            async Task Step(string label, Action change)
            {
                change();
                App.NotifySettingsChanged();
                await Task.Delay(800);
                place.AppendLine($"   {label,-28} {ov.CheckPlacement()}");
            }
            await Step("pin top-right, pill", () => st.OverlayLayout = OverlayLayout.Pill);
            await Step("-> card", () => st.OverlayLayout = OverlayLayout.Card);
            await Step("-> rings", () => st.OverlayLayout = OverlayLayout.Rings);
            await Step("-> pill", () => st.OverlayLayout = OverlayLayout.Pill);
            await Step("+ usage & power", () => { st.OverlayShowUsage = true; st.OverlayShowPower = true; });
            await Step("- usage & power", () => { st.OverlayShowUsage = false; st.OverlayShowPower = false; });
            await Step("pin bottom-left, card", () => { st.OverlayAnchor = OverlayAnchor.BottomLeft; st.OverlayLayout = OverlayLayout.Card; });
            await Step("-> pill", () => st.OverlayLayout = OverlayLayout.Pill);
            OverlayWindow.TraceReposition = false;
            (st.OverlayLayout, st.OverlayAnchor, st.OverlayShowUsage, st.OverlayShowPower) = keep;
            App.NotifySettingsChanged();
            await Task.Delay(500);
            File.AppendAllText(Path.Combine(dir, "sensors.txt"), place.ToString());
        }

        // ---- tray icons (2-digit, 3-digit °F, warm) ----
        foreach (var (txt, sev, name) in new[] { ("51", Severity.Normal, "51"), ("124", Severity.Normal, "124"), ("88", Severity.Warm, "88warm"), ("7", Severity.Normal, "7") })
            using (var bmp = TrayService.RenderTempIcon(txt, sev, 40))
                bmp.Save(Path.Combine(dir, $"tray_{name}.png"), System.Drawing.Imaging.ImageFormat.Png);

        // ---- main window pages ----
        var settings = App.Settings;
        main.Width = 1280; main.Height = 900;
        // Longest gap between rendered frames while each page opens for the first time: a freeze
        // shows up as one long gap (60 Hz ≈ 17 ms per frame).
        var ui = new StringBuilder("\nPage switch smoothness (first open, longest frame gap / time in the click handler):\n");
        foreach (var nav in new[] { "NavDashboard", "NavSensors", "NavLog", "NavOverlay", "NavSettings" })
        {
            var last = System.Diagnostics.Stopwatch.GetTimestamp();
            double worst = 0;
            EventHandler onFrame = (_, _) =>
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                worst = Math.Max(worst, (now - last) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                last = now;
            };
            await Task.Delay(300);
            CompositionTarget.Rendering += onFrame;
            last = System.Diagnostics.Stopwatch.GetTimestamp();
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (main.FindName(nav) is RadioButton rb) rb.IsChecked = true;
            var clickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            main.UpdateSnapshot(snap);
            await Task.Delay(1200);
            CompositionTarget.Rendering -= onFrame;
            ui.AppendLine($"   {nav[3..],-10} {worst,6:0} ms   {clickMs,6:0} ms");
            Render(main, Path.Combine(dir, $"main_{nav[3..].ToLower()}.png"), true);
            if (nav == "NavLog" && (main.FindName("PageHost") as ContentControl)?.Content is Views.Pages.LogPage lp)
            {
                lp.Scroller.ScrollToEnd();
                await Task.Delay(400);
                Render(main, Path.Combine(dir, "main_log_end.png"), true);
                lp.Scroller.ScrollToHome();
                int savedView = settings.LogViewHours;
                foreach (var h in ViewModels.LogViewModel.ViewOptions(settings.LogRetentionDays))
                {
                    lp.SetRange(h);
                    await Task.Delay(900);
                    Render(main, Path.Combine(dir, h < 24 ? $"main_log_{h}h.png" : $"main_log_{h / 24}d.png"), true);
                }
                lp.SetRange(savedView);
            }
            if (nav == "NavSettings" && (main.FindName("PageHost") as ContentControl)?.Content is Views.Pages.SettingsPage stp)
            {
                stp.Scroller.ScrollToVerticalOffset(1250);
                await Task.Delay(400);
                Render(main, Path.Combine(dir, "main_settings_log.png"), true);
                stp.Scroller.ScrollToEnd();
                await Task.Delay(400);
                Render(main, Path.Combine(dir, "main_settings_end.png"), true);
                stp.Scroller.ScrollToHome();
            }
            if (nav == "NavSensors")
            {
                int items = (main.FindName("PageHost") as ContentControl)?.Content is Views.Pages.SensorsPage sp && sp.FindName("List") is ItemsControl ic ? ic.Items.Count : -1;
                int realized = CountRows(main);
                ui.AppendLine($"   All sensors list: {items} items, {realized} row visuals built (virtualized: only the visible ones)");
                // Collapsed cards + one expanded card (checks the rounded card slices line up).
                if ((main.FindName("PageHost") as ContentControl)?.Content is FrameworkElement { DataContext: ViewModels.SensorsViewModel svm })
                {
                    var groups = svm.Items.OfType<ViewModels.SensorGroupVM>().ToList();
                    foreach (var g in groups) if (g.IsExpanded) svm.Toggle(g);
                    if (groups.Count > 0) svm.Toggle(groups[^1]);
                    await Task.Delay(500);
                    Render(main, Path.Combine(dir, "main_sensors_collapsed.png"), true);
                    foreach (var g in groups) if (!g.IsExpanded) svm.Toggle(g);
                }
            }
        }
        if (main.FindName("NavDashboard") is RadioButton d0) d0.IsChecked = true;
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        ui.AppendLine($"Signature check: dotnet.exe (signed) = {DriverService.HasValidSignature(dotnet)}, Ember.exe (unsigned) = {DriverService.HasValidSignature(Environment.ProcessPath!)}");
        File.AppendAllText(Path.Combine(dir, "sensors.txt"), ui.ToString());

        // ---- overlay styles ----
        var saved = (settings.OverlayLayout, settings.OverlayShowRam, settings.OverlayShowDrives, settings.OverlayShowUsage, settings.OverlayShowPower);
        foreach (var layout in Enum.GetValues<OverlayLayout>())
        {
            foreach (var full in new[] { false, true })
            {
                settings.OverlayLayout = layout;
                settings.OverlayShowRam = full; settings.OverlayShowDrives = full;
                settings.OverlayShowUsage = full; settings.OverlayShowPower = full;
                var vm = new OverlayViewModel();
                vm.Update(snap);
                var oc = new OverlayContent { IsPreview = true, DataContext = vm };
                var host = new Border
                {
                    Padding = new Thickness(30),
                    Background = new LinearGradientBrush(Color.FromRgb(0x2B, 0x33, 0x50), Color.FromRgb(0x55, 0x36, 0x2E), 30),
                    Child = oc,
                };
                oc.ApplyAppearance();
                host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();
                await Task.Delay(700); // let gauge animations finish
                host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();
                Render(host, Path.Combine(dir, $"overlay_{layout.ToString().ToLower()}{(full ? "_full" : "")}.png"), false);
            }
        }
        // ---- unlocked vs locked size (must be identical) ----
        var sizes = new StringBuilder();
        var savedLock = settings.OverlayLocked;
        foreach (var locked in new[] { true, false })
        {
            settings.OverlayLocked = locked;
            settings.OverlayLayout = OverlayLayout.Pill;
            var vm = new OverlayViewModel(); vm.Update(snap);
            var oc = new OverlayContent { DataContext = vm };
            oc.ApplyAppearance();
            var host = new Border { Padding = new Thickness(30), Background = new SolidColorBrush(Color.FromRgb(0x30, 0x2A, 0x45)), Child = oc };
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
            sizes.AppendLine($"locked={locked}: {oc.ActualWidth:0.0} x {oc.ActualHeight:0.0}");
            Render(host, Path.Combine(dir, $"overlay_pill_{(locked ? "locked" : "unlocked")}.png"), false);
        }
        settings.OverlayLocked = savedLock;

        // ---- tabular digits: "11" and "48" must measure the same ----
        foreach (var txt in new[] { "11", "48", "41", "42", "100" })
        {
            var tb = new TextBlock { Text = txt, FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont") };
            System.Windows.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            sizes.AppendLine($"digits '{txt}': {tb.DesiredSize.Width:0.00}");
        }
        // ---- usage / power extras: the overlay must keep one size while they change ----
        var savedExtras = (settings.OverlayShowUsage, settings.OverlayShowPower, settings.OverlayShowRam, settings.OverlayLayout);
        settings.OverlayShowUsage = true; settings.OverlayShowPower = true; settings.OverlayShowRam = true;
        foreach (var layout in new[] { OverlayLayout.Pill, OverlayLayout.Card, OverlayLayout.Rings })
        {
            settings.OverlayLayout = layout;
            var vm = new OverlayViewModel(); vm.Update(snap);
            var oc = new OverlayContent { DataContext = vm };
            oc.ApplyAppearance();
            var host = new Border { Child = oc };
            var widths = new System.Collections.Generic.List<string>();
            foreach (var extra in new[] { "3% · 9W", "45% · 120W", "99% · 999W", "–% · –W", "7% · 65W" })
            {
                foreach (var it in vm.Items) if (it.HasExtra) it.Extra = it.Key == "ram" ? extra.Split(' ')[0] : extra;
                host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                host.Arrange(new Rect(host.DesiredSize)); host.UpdateLayout();
                widths.Add($"{oc.ActualWidth:0.00}x{oc.ActualHeight:0.00}");
            }
            sizes.AppendLine($"extras {layout}: {string.Join("  ", widths)}  {(widths.Distinct().Count() == 1 ? "STABLE" : "CHANGES")}");
        }
        (settings.OverlayShowUsage, settings.OverlayShowPower, settings.OverlayShowRam, settings.OverlayLayout) = savedExtras;

        sizes.AppendLine("Live overlay size history:");
        foreach (var l in OverlayWindow.SizeLog) sizes.AppendLine("   " + l);
        File.AppendAllText(Path.Combine(dir, "sensors.txt"), "\nOverlay sizes:\n" + sizes);
        await Task.CompletedTask;

        (settings.OverlayLayout, settings.OverlayShowRam, settings.OverlayShowDrives, settings.OverlayShowUsage, settings.OverlayShowPower) = saved;
        main.Width = settings.MainWidth; main.Height = settings.MainHeight; // don't persist the capture size
    }

    /// <summary>`--profile file`: measures process CPU in several UI states (diagnostics).</summary>
    public static async Task Profile(string file, MainWindow main)
    {
        var sb = new StringBuilder();
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        await Task.Delay(20000); // warm-up (JIT, first SMART reads)
        async Task Measure(string label)
        {
            await Task.Delay(3000);
            proc.Refresh();
            double p0 = proc.TotalProcessorTime.TotalMilliseconds, s0 = App.Sensors.PollThreadCpuMs, u0 = SensorService.ThreadCpuMs();
            await Task.Delay(15000);
            proc.Refresh();
            double p1 = proc.TotalProcessorTime.TotalMilliseconds, s1 = App.Sensors.PollThreadCpuMs, u1 = SensorService.ThreadCpuMs();
            sb.AppendLine($"{label,-40} process {(p1 - p0) / 150,5:0.0}%  poll {(s1 - s0) / 150,5:0.0}%  ui {(u1 - u0) / 150,5:0.0}%  (of one core)");
            File.WriteAllText(file, sb.ToString());
        }
        var s = App.Settings;
        await Measure("main visible + overlay (pill)");
        main.Hide(); App.Current.UpdateOverlayVisibility();
        await Measure("main hidden + overlay (pill)");
        s.OverlayLayout = OverlayLayout.Rings; App.NotifySettingsChanged();
        await Measure("main hidden + overlay (rings)");
        s.OverlayLayout = OverlayLayout.Pill; s.OverlayVisible = false; App.NotifySettingsChanged();
        await Measure("main hidden + overlay hidden");
        s.OverlayVisible = true; App.NotifySettingsChanged();
    }

    private static void Render(FrameworkElement el, string path, bool window)
    {
        double w = window ? ((Window)el).ActualWidth : el.ActualWidth;
        double h = window ? ((Window)el).ActualHeight : el.ActualHeight;
        var visual = window ? (Visual)((Window)el).Content : el;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * 1.25), (int)Math.Ceiling(h * 1.25), 120, 120, PixelFormats.Pbgra32);
        if (window)
        {
            // Draw an opaque backdrop approximation behind the transparent window content.
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x26)), null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, w, h));
            }
            rtb.Render(dv);
        }
        else rtb.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
