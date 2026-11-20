// LogTool: checks the temperature log engine and creates sample logs.
//   dotnet run --project tools/LogTool -- selftest
//   dotnet run --project tools/LogTool -- seed <file> <days>     realistic sample data ending now
using Ember.Models;
using Ember.Services;
using Ember.ViewModels;

int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

static DeviceSnapshot Dev(string id, string name, DeviceKind kind, double t, DriveMedia media = DriveMedia.Ssd) =>
    new(id, name, kind, kind.ToString(), Array.Empty<SensorReading>())
    { PrimaryTemp = new SensorReading(id + "/t", "Temp", "Temperature", t, null, null), Media = media };

static SensorSnapshot Snap(double cpu, double gpu, double ram, double ssd, double hdd) => new()
{
    Cpu = Dev("/cpu/0", "Test CPU", DeviceKind.Cpu, cpu),
    Gpu = Dev("/gpu/0", "Test GPU", DeviceKind.Gpu, gpu),
    RamTemps = new[] { new SensorReading("/ram/0", "DIMM", "Temperature", ram, null, null) },
    Drives = new[] { Dev("/nvme/0", "Test NVMe 1TB", DeviceKind.Storage, ssd), Dev("/hdd/1", "Test HDD 3TB", DeviceKind.Storage, hdd, DriveMedia.Hdd) },
};

// Realistic pattern: idle most of the day, a gaming session in the evening, PC off overnight.
static (bool on, double cpu, double gpu) Pattern(DateTime local, Random rnd)
{
    double h = local.Hour + local.Minute / 60.0;
    if (h < 7.5) return (false, 0, 0);                           // off overnight
    bool gaming = h >= 19 && h < 22.5 && local.DayOfWeek != DayOfWeek.Monday;
    bool work = h >= 9 && h < 17;
    double cpu = gaming ? 68 + 10 * Math.Sin(h * 3) : work ? 48 + 6 * Math.Sin(h * 5) : 42;
    double gpu = gaming ? 66 + 8 * Math.Cos(h * 2) : 38;
    if (local.DayOfWeek == DayOfWeek.Saturday && gaming) { cpu += 8; gpu += 9; } // a hot session
    return (true, cpu + rnd.NextDouble() * 4 - 2, gpu + rnd.NextDouble() * 3 - 1.5);
}

static long Simulate(TemperatureLog log, DateTime startUtc, DateTime endUtc, int sampleSeconds, Random rnd, bool honourPattern = true)
{
    long flushes = 0;
    var nextFlush = startUtc.AddMinutes(10);
    for (var t = startUtc; t < endUtc; t = t.AddSeconds(sampleSeconds))
    {
        var p = honourPattern ? Pattern(t.ToLocalTime(), rnd) : (true, 50 + rnd.NextDouble() * 10, 45.0);
        if (p.Item1)
        {
            double ram = 38 + (p.Item2 - 40) * 0.25, ssd = 36 + (p.Item2 - 40) * 0.3, hdd = 33 + (p.Item2 - 40) * 0.15;
            log.Add(Snap(p.Item2, p.Item3, ram, ssd, hdd), t);
        }
        if (t >= nextFlush) { log.Flush(t); flushes++; nextFlush = t.AddMinutes(10); }
    }
    log.Flush(endUtc, includeCurrent: true);
    return flushes;
}

if (args.Length >= 3 && args[0] == "seed")
{
    var path = Path.GetFullPath(args[1]); int days = int.Parse(args[2]);
    if (File.Exists(path)) File.Delete(path);
    using var log = TemperatureLog.Open(path, TemperatureLog.RowsForDays(Math.Max(7, days)));
    var end = DateTime.UtcNow;
    Simulate(log, end.AddDays(-days), end, 5, new Random(7));
    var data = log.Read(TemperatureLog.Minute(end) - TemperatureLog.RowsForDays(days), TemperatureLog.Minute(end));
    Console.WriteLine($"seeded {path}: {new FileInfo(path).Length} bytes, {data.Count} components, {data.FirstOrDefault()?.Points.Length} minutes");
    return 0;
}
if (args.Length >= 2 && args[0] == "info")
{
    // Read-only look at a log: copy it first so a running Ember isn't disturbed.
    var copy = Path.Combine(Path.GetTempPath(), "ember-loginfo-" + Guid.NewGuid().ToString("N")[..8] + ".bin");
    File.Copy(args[1], copy);
    try
    {
        var len = new FileInfo(copy).Length;
        int cap = (int)((len - TemperatureLog.HeaderBytes) / TemperatureLog.RowBytes);
        using var log = TemperatureLog.Open(copy, cap);
        long now = TemperatureLog.Minute(DateTime.UtcNow);
        var all = log.Read(now - cap + 1, now);
        Console.WriteLine($"{args[1]}: {len:N0} bytes, keeps {cap / 1440.0:0.#} days");
        foreach (var s in all)
            Console.WriteLine($"  {s.Series.Key,-40} {s.Series.Label,-30} {s.Points.Length,6} min  last {s.Points[^1].LocalTime:g}  avg {s.Points.Average(p => p.Avg):0.0}°C");
    }
    finally { File.Delete(copy); }
    return 0;
}
if (args.Length == 0 || args[0] != "selftest") { Console.WriteLine("usage: LogTool selftest | seed <file> <days> | info <file>"); return 2; }

var dir = Path.Combine(Path.GetTempPath(), "ember-logtest-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var file = Path.Combine(dir, "log.bin");
try
{
    var rnd = new Random(1);
    var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    // 1. Eight days into a seven-day log: fixed size, oldest day overwritten.
    var log = TemperatureLog.Open(file, TemperatureLog.RowsForDays(7));
    long size0 = new FileInfo(file).Length;
    Check(size0 == TemperatureLog.FileBytesForDays(7), $"file created at full size: {size0:N0} bytes for 7 days");
    var end = start.AddDays(8);
    long flushes = Simulate(log, start, end, 2, rnd, honourPattern: false);
    long size1 = new FileInfo(file).Length;
    Check(size1 == size0, $"size unchanged after 8 days of recording ({size1:N0} bytes)");
    long nowM = TemperatureLog.Minute(end);
    var week = log.Read(nowM - TemperatureLog.RowsForDays(7) + 1, nowM);
    var cpu = week.Single(s => s.Series.Key == "cpu");
    Check(week.Count == 5, $"5 components recorded (cpu, gpu, ram, nvme, hdd): {week.Count}");
    // The final minute ('end') has no samples yet, so a full week is 7 × 1440 − 1 recorded minutes.
    Check(cpu.Points.Length == TemperatureLog.RowsForDays(7) - 1, $"7 days of minutes kept: {cpu.Points.Length}");
    Check(cpu.Points[0].Minute == nowM - TemperatureLog.RowsForDays(7) + 1, "oldest minute is exactly 7 days back (day 1 overwritten)");
    Check(cpu.Points.All(p => p.Min <= p.Avg && p.Avg <= p.Max && p.Min >= 49.5 && p.Max <= 60.5), "min ≤ avg ≤ max and values in the fed range");
    double perDay = log.BytesWritten / 8.0;
    Check(perDay < 100_000, $"written per day: {perDay / 1024:0.0} KB in {flushes / 8.0:0} writes of ~{log.BytesWritten / (double)Math.Max(1, log.Writes):0} bytes");
    log.Dispose();

    // 2. Persistence across restarts.
    log = TemperatureLog.Open(file, TemperatureLog.RowsForDays(7));
    var again = log.Read(nowM - TemperatureLog.RowsForDays(7) + 1, nowM).Single(s => s.Series.Key == "cpu");
    Check(again.Points.Length == cpu.Points.Length && again.Points[^1] == cpu.Points[^1], "same data after reopening");

    // 3. Gap: PC off for 3 hours records nothing; recording resumes after.
    var gapStart = end; var resume = end.AddHours(3);
    Simulate(log, resume, resume.AddMinutes(30), 2, rnd, honourPattern: false);
    long m2 = TemperatureLog.Minute(resume.AddMinutes(30));
    var withGap = log.Read(TemperatureLog.Minute(gapStart) - 60, m2).Single(s => s.Series.Key == "cpu");
    bool noneInGap = withGap.Points.All(p => p.Minute < TemperatureLog.Minute(gapStart) || p.Minute >= TemperatureLog.Minute(resume));
    Check(noneInGap && withGap.Points.Count(p => p.Minute >= TemperatureLog.Minute(resume)) >= 29, "3-hour gap left empty, recording resumed after it");

    // 4a. A bigger buffer keeps everything already recorded; back to 7 days keeps the newest week.
    log.Resize(TemperatureLog.RowsForDays(30));
    Check(new FileInfo(file).Length == TemperatureLog.FileBytesForDays(30), $"resized to 30 days: {new FileInfo(file).Length:N0} bytes");
    var r30 = log.Read(m2 - TemperatureLog.RowsForDays(30) + 1, m2).Single(s => s.Series.Key == "cpu");
    Check(r30.Points.Length >= TemperatureLog.RowsForDays(7) - 200, $"data kept when growing ({r30.Points.Length} minutes)");
    log.Resize(TemperatureLog.RowsForDays(7));
    Check(new FileInfo(file).Length == TemperatureLog.FileBytesForDays(7), "back to 7 days");

    // 4b. Stale rows: after 10 days off, nothing older than 7 days shows.
    var later = resume.AddDays(10);
    Simulate(log, later, later.AddMinutes(20), 2, rnd, honourPattern: false);
    long m3 = TemperatureLog.Minute(later.AddMinutes(20));
    var afterLongGap = log.Read(m3 - TemperatureLog.RowsForDays(7) + 1, m3).Single(s => s.Series.Key == "cpu");
    Check(afterLongGap.Points.Length is >= 19 and <= 21, $"after 10 days off only the new minutes show ({afterLongGap.Points.Length})");

    // 5. A smaller buffer keeps the newest minutes.
    log.Resize(TemperatureLog.RowsForDays(1));
    var r1 = log.Read(m3 - TemperatureLog.RowsForDays(1) + 1, m3).Single(s => s.Series.Key == "cpu");
    Check(new FileInfo(file).Length == TemperatureLog.FileBytesForDays(1) && r1.Points.Length is >= 19 and <= 21, "shrunk to 1 day, newest minutes kept");
    Check(!File.Exists(file + ".resize"), "no temporary file left behind");
    log.Dispose();

    // 6. Encoding: 0.5 °C steps, clamped, nonsense ignored.
    var f2 = Path.Combine(dir, "enc.bin");
    using (var e = TemperatureLog.Open(f2, 120))
    {
        var t0 = start;
        e.Add(Snap(45.3, 200, double.NaN, 0, -5), t0);
        e.Add(Snap(45.1, 130, double.NaN, 0, -5), t0.AddSeconds(10));
        e.Flush(t0.AddMinutes(1), includeCurrent: true);
        var d = e.Read(0, TemperatureLog.Minute(t0) + 1);
        var c = d.Single(s => s.Series.Key == "cpu").Points.Single();
        Check(c.Min == 45 && c.Max == 45.5 && c.Avg == 45, $"0.5 °C resolution (min {c.Min}, avg {c.Avg}, max {c.Max})");
        var g = d.Single(s => s.Series.Key == "gpu").Points.Single();
        Check(g.Max == 127.5, $"high values clamped to 127.5 (got {g.Max}); 200 °C ignored as a bad reading");
        Check(d.All(s => s.Series.Key is "cpu" or "gpu"), "NaN, zero and negative readings ignored");
    }

    // 7. A damaged file is set aside and a new one started.
    var f3 = Path.Combine(dir, "bad.bin");
    File.WriteAllBytes(f3, new byte[5000]);
    using (var b = TemperatureLog.Open(f3, TemperatureLog.RowsForDays(7)))
        Check(File.Exists(f3 + ".corrupt") && new FileInfo(f3).Length == TemperatureLog.FileBytesForDays(7), "damaged file kept as .corrupt, fresh log created");

    // 8. Component limit.
    using (var lim = TemperatureLog.Open(Path.Combine(dir, "lim.bin"), 120))
    {
        var many = new SensorSnapshot { Drives = Enumerable.Range(0, 25).Select(i => Dev($"/d/{i}", $"Drive {i}", DeviceKind.Storage, 40)).ToArray() };
        lim.Add(many, start);
        Check(lim.Series.Count == TemperatureLog.MaxSeries, $"at most {TemperatureLog.MaxSeries} components ({lim.Series.Count})");
    }

    // 9. Analysis helpers.
    var pts = new List<LogPoint>();
    for (int i = 0; i < 120; i++) pts.Add(new LogPoint(1000 + i, 40, i < 100 ? 50 : 85, i < 100 ? 55 : 92));
    var st = LogAnalysis.Summarize(pts, 80, 90)!;
    Check(st.Peak == 92 && st.MinutesHot == 20 && st.MinutesWarm == 0 && Math.Abs(st.Avg - (100 * 50 + 20 * 85) / 120.0) < 1e-9, "summary: peak, time above critical, average");
    var bins = LogAnalysis.Downsample(pts, 1000, 1239, 12);
    Check(bins.Length == 12 && !double.IsNaN(bins[0].Avg) && double.IsNaN(bins[^1].Avg), "downsampling marks empty stretches as gaps");

    // 9b. Time axis: a sensible number of grid lines for every chart length.
    long nowT = TemperatureLog.Minute(DateTime.UtcNow);
    var tickCounts = LogViewModel.ViewOptions(365).Select(h => (h, n: LogAnalysis.Ticks(nowT - h * 60 + 1, nowT).Count)).ToList();
    Check(tickCounts.All(t => t.n is >= 4 and <= 16), "grid lines per range: " + string.Join(", ", tickCounts.Select(t => $"{LogViewModel.RangeText(t.h)}={t.n}")));
    Check(string.Join(",", LogViewModel.ViewOptions(1)) == "1,3,6,12,24" && LogViewModel.ViewOptions(7).Last() == 168 && LogViewModel.ViewOptions(10).Last() == 240,
          "chart lengths: hours first, then days up to what the log keeps");
    Check(LogViewModel.RangeText(1) == "1 hour" && LogViewModel.RangeText(3) == "3 hours" && LogViewModel.RangeText(24) == "24 hours" && LogViewModel.RangeText(4320) == "6 months",
          "chart length names");
    // Settings from 1.2.0 (chart length in days) load as hours and are saved without the old field.
    var cfgDir = Path.Combine(dir, "cfg");
    Directory.CreateDirectory(cfgDir);
    Environment.SetEnvironmentVariable("EMBER_CONFIG_DIR", cfgDir);
    File.WriteAllText(Path.Combine(cfgDir, "settings.json"), "{\"LogRetentionDays\":30,\"LogViewDays\":14}");
    var oldCfg = AppSettings.Load();
    oldCfg.Save();
    var savedCfg = File.ReadAllText(Path.Combine(cfgDir, "settings.json"));
    Check(oldCfg.LogViewHours == 14 * 24 && LogViewModel.ViewHours(oldCfg) == 14 * 24 && !savedCfg.Contains("LogViewDays") && savedCfg.Contains("\"LogViewHours\": 336"),
          "1.2.0 chart length (14 days) carries over as 336 hours");
    // Overlay presets: capture → change → apply restores; saved and loaded intact; hotkey clashes found.
    var ps = new AppSettings { OverlayLayout = OverlayLayout.Card, OverlayAnchor = OverlayAnchor.BottomLeft, OverlayMonitor = "\\\\.\\DISPLAY2",
                               OverlayShowRam = true, OverlayScale = 1.3 };
    ps.Drives.Add(new DriveOverlayOption { Id = "/nvme/0", Show = false, Label = "Games" });
    var desk = OverlayPreset.Capture(ps, "Desk");
    desk.Hotkey = new List<int> { 0xA2, 0x31 }; // Left Ctrl + 1
    ps.OverlayPresets.Add(desk);
    Check(desk.Matches(ps), "a preset matches the setup it was saved from");
    ps.OverlayLayout = OverlayLayout.Pill; ps.OverlayAnchor = OverlayAnchor.Free; ps.OverlayFreeX = 100; ps.OverlayFreeY = 50;
    ps.OverlayShowRam = false; ps.Drives[0].Show = true; ps.OverlayVisible = false;
    var game = OverlayPreset.Capture(ps, "Game");
    ps.OverlayPresets.Add(game);
    Check(!desk.Matches(ps) && game.Matches(ps), "after changing the overlay, only the new preset matches");
    desk.ApplyTo(ps);
    Check(desk.Matches(ps) && ps.OverlayLayout == OverlayLayout.Card && ps.OverlayAnchor == OverlayAnchor.BottomLeft && ps.OverlayShowRam
          && !ps.Drives[0].Show && ps.OverlayVisible, "applying a preset restores style, position, readings and drive choices, and shows the overlay");
    Check(ps.HotkeyOwner(new[] { 0x31, 0xA2 }, game.Id) == "Desk" && ps.HotkeyOwner(new[] { 0xA2, 0x31 }, desk.Id) == null
          && ps.HotkeyOwner(new[] { 0xA3, 0xA5 }, game.Id) == "Show / hide overlay" && ps.HotkeyOwner(new[] { 0xA3, 0xA5 }, "") == null,
          "hotkey clashes: found for other presets and the show / hide key, not for the same preset");
    Check(desk.Describe("Display 2") == "Card · Display 2, bottom left · CPU, GPU, RAM" && game.Describe("Display 1").Contains("where you placed it"),
          $"preset summary: \"{desk.Describe("Display 2")}\"");
    var presetDir = Path.Combine(dir, "presets");
    Directory.CreateDirectory(presetDir);
    Environment.SetEnvironmentVariable("EMBER_CONFIG_DIR", presetDir);
    ps.OverlayPresets.Add(new OverlayPreset { Id = desk.Id, Name = "" }); // damaged: duplicate id, no name
    ps.Save();
    var back = AppSettings.Load();
    Check(back.OverlayPresets.Count == 3 && back.OverlayPresets[0].Name == "Desk" && back.OverlayPresets[0].Hotkey.SequenceEqual(new[] { 0xA2, 0x31 })
          && back.OverlayPresets[1].FreeX == 100 && back.OverlayPresets[1].Anchor == OverlayAnchor.Free && double.IsNaN(back.OverlayPresets[0].FreeX)
          && back.OverlayPresets[2].Id != desk.Id && back.OverlayPresets[2].Name == "Preset",
          "presets survive saving and loading (unset position kept, duplicate id and empty name repaired)");
    Environment.SetEnvironmentVariable("EMBER_CONFIG_DIR", null);
    Check(LogAnalysis.Duration(0) == "Never", "no time above warning reads \"Never\"");

    // 9c. Reading speed on a full year (what the Log page does on every refresh).
    var fy = Path.Combine(dir, "year.bin");
    using (var y = TemperatureLog.Open(fy, TemperatureLog.RowsForDays(365)))
    {
        var e3 = DateTime.UtcNow;
        Simulate(y, e3.AddDays(-365), e3, 30, new Random(5));
        long n3 = TemperatureLog.Minute(e3);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var yr = y.Read(n3 - TemperatureLog.RowsForDays(365) + 1, n3);
        long msYear = sw.ElapsedMilliseconds; sw.Restart();
        var mo = y.Read(n3 - TemperatureLog.RowsForDays(30) + 1, n3);
        long msMonth = sw.ElapsedMilliseconds;
        int yrMin = yr.Single(s => s.Series.Key == "cpu").Points.Length, moMin = mo.Single(s => s.Series.Key == "cpu").Points.Length;
        Check(msYear < 1000 && msMonth < 150 && yrMin > moMin * 10, $"reading a 1-year log: whole year {msYear} ms ({yrMin:N0} min), last 30 days {msMonth} ms ({moMin:N0} min)");
        // The Log page's whole job for the 1-year view (runs once a minute while the page is open).
        var cfgY = new AppSettings { LogRetentionDays = 365 };
        LogViewModel.Build(y, 365 * 24, cfgY, e3); // warm up
        long alloc0 = GC.GetAllocatedBytesForCurrentThread(); sw.Restart();
        var pageYear = LogViewModel.Build(y, 365 * 24, cfgY, e3);
        long msPage = sw.ElapsedMilliseconds, mbPage = (GC.GetAllocatedBytesForCurrentThread() - alloc0) / (1024 * 1024);
        Check(pageYear.Cards.Count == 5 && pageYear.Days.Count >= 300 && msPage < 1500 && mbPage < 120,
              $"Log page for a full year: {msPage} ms, {mbPage} MB allocated, {pageYear.Days.Count} daily rows");
        // Daily summary matches a straightforward per-day grouping.
        var cpuYear = yr.Single(s => s.Series.Key == "cpu").Points;
        var simple = cpuYear.GroupBy(p => p.LocalTime.Date).OrderByDescending(g => g.Key)
                            .Select(g => (g.Key, g.Min(p => p.Min), g.Average(p => p.Avg), g.Max(p => p.Max), g.Count())).ToList();
        var fast = LogAnalysis.Daily(cpuYear);
        Check(fast.Count == simple.Count && fast.Zip(simple).All(z => z.First.Date == z.Second.Key && z.First.Min == z.Second.Item2 &&
              Math.Abs(z.First.Avg - z.Second.Item3) < 1e-9 && z.First.Max == z.Second.Item4 && z.First.Minutes == z.Second.Item5),
              $"daily summary matches a plain per-day grouping ({fast.Count} days)");
        // Minutes not yet written to disk are part of what's read.
        var tail = e3.AddMinutes(3);
        y.Add(Snap(61, 51, 40, 38, 35), tail);
        var unsaved = y.Read(TemperatureLog.Minute(tail) - 5, TemperatureLog.Minute(tail)).Single(s => s.Series.Key == "cpu").Points;
        Check(unsaved[^1].Minute == TemperatureLog.Minute(tail) && unsaved[^1].Max == 61, "unsaved minutes show before they're written");
    }

    // 10. Seeded realistic week (used for the UI): readable and plausible.
    var f4 = Path.Combine(dir, "seed.bin");
    using (var s = TemperatureLog.Open(f4, TemperatureLog.RowsForDays(7)))
    {
        var e2 = DateTime.UtcNow;
        Simulate(s, e2.AddDays(-7), e2, 5, new Random(3));
        var sd = s.Read(TemperatureLog.Minute(e2) - TemperatureLog.RowsForDays(7) + 1, TemperatureLog.Minute(e2));
        var days = LogAnalysis.Daily(sd.Single(x => x.Series.Key == "cpu").Points);
        Check(days.Count is >= 7 and <= 8, $"daily summary has {days.Count} days");
    }

    // 9d. Short chart lengths: page status and the daily summary.
    var cfgS = new AppSettings();
    using (var g = TemperatureLog.Open(Path.Combine(dir, "gap.bin"), TemperatureLog.RowsForDays(7)))
    {
        var e5 = DateTime.UtcNow;
        Simulate(g, e5.AddMinutes(-30), e5, 10, new Random(11), honourPattern: false);
        var fresh = LogViewModel.Build(g, 1, cfgS, e5);
        Check(fresh.Status.StartsWith("Recording since") && fresh.Cards[0].Bins.Length == 60 && fresh.Days.Count == 0,
              $"new log, 1 hour view: \"{fresh.Status}\", one column a minute, no daily summary");
        Simulate(g, e5.AddDays(-2), e5.AddHours(-3), 10, new Random(12), honourPattern: false);
        var gap = LogViewModel.Build(g, 1, cfgS, e5);
        Check(gap.Status == "Showing the last 1 hour", $"closed for a while before the hour: \"{gap.Status}\" (not \"Recording since\")");
        Check(LogViewModel.Build(g, 24, cfgS, e5).Days.Count > 0, "24 hour view keeps the daily summary");
    }
}
finally
{
    try { Directory.Delete(dir, true); } catch { }
}
Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) failed.");
return failures == 0 ? 0 : 1;
