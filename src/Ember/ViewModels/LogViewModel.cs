using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ember.Models;
using Ember.Services;

namespace Ember.ViewModels;

/// <summary>One component on the Log page: headline numbers and the chart data.</summary>
public sealed class LogCardVM
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Average { get; init; } = "";
    public string Peak { get; init; } = "";
    public string PeakWhen { get; init; } = "";
    public string Low { get; init; } = "";
    public string AboveWarning { get; init; } = "";
    public string AboveCritical { get; init; } = "";
    public bool HasHotTime { get; init; }
    public LogAnalysis.Bin[] Bins { get; init; } = Array.Empty<LogAnalysis.Bin>();
    public long FromMinute { get; init; }
    public long ToMinute { get; init; }
    public double Warn { get; init; }
    public double Critical { get; init; }
}

/// <summary>A row of the daily summary: one cell per column ("avg · peak"), empty when not recorded.</summary>
public sealed class LogDayVM
{
    public string Day { get; init; } = "";
    public string[] Cells { get; init; } = Array.Empty<string>();
    public bool[] Hot { get; init; } = Array.Empty<bool>();
}

public sealed class LogView
{
    public List<LogCardVM> Cards { get; } = new();
    public string[] DayColumns { get; init; } = Array.Empty<string>();
    public List<LogDayVM> Days { get; } = new();
    public string Status { get; init; } = "";
    public bool HasData => Cards.Count > 0;
}

/// <summary>Turns the raw log into what the Log page shows (shared by the Windows and Linux apps).</summary>
public static class LogViewModel
{
    private static readonly int[] ViewLengths = { 1, 7, 14, 30, 60, 90, 180, 365 };

    /// <summary>Chart lengths offered on the Log page: the standard steps up to what the log keeps, plus the full log.</summary>
    public static List<int> ViewOptions(int retentionDays)
    {
        var list = ViewLengths.Where(d => d <= retentionDays).ToList();
        if (!list.Contains(retentionDays)) list.Add(retentionDays);
        return list;
    }

    /// <summary>The saved chart length, limited to what the log keeps.</summary>
    public static int ViewDays(AppSettings s) =>
        ViewOptions(s.LogRetentionDays).LastOrDefault(d => d <= Math.Max(1, s.LogViewDays), 1);

    public static string DaysText(int days) => days switch
    {
        1 => "1 day",
        180 => "6 months",
        365 => "1 year",
        _ => $"{days} days",
    };

    /// <summary>Text for "the last …".</summary>
    private static string LastText(int days) => days == 1 ? "24 hours" : DaysText(days);

    public static string SizeText(long bytes) =>
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    private static Thresholds Limits(LogSeriesKind k, AppSettings s) => k switch
    {
        LogSeriesKind.Cpu => s.CpuThresholds,
        LogSeriesKind.Gpu => s.GpuThresholds,
        LogSeriesKind.Ram => s.RamThresholds,
        LogSeriesKind.Hdd => s.HddThresholds,
        _ => s.SsdThresholds,
    };

    private static string KindText(LogSeriesKind k) => k switch
    {
        LogSeriesKind.Cpu => "Processor",
        LogSeriesKind.Gpu => "Graphics",
        LogSeriesKind.Ram => "Hottest memory module",
        LogSeriesKind.Hdd => "Hard drive",
        _ => "SSD",
    };

    private static string T(double c) => Temps.Number(c) + "°";

    private static string When(DateTime local)
    {
        var today = DateTime.Now.Date;
        string day = local.Date == today ? "today" : local.Date == today.AddDays(-1) ? "yesterday" : local.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        return $"{day}, {local.ToString("t", CultureInfo.CurrentCulture)}";
    }

    public static LogView Build(TemperatureLog log, int days, AppSettings s, DateTime utcNow, int bins = 360)
    {
        long to = TemperatureLog.Minute(utcNow);
        long from = to - TemperatureLog.RowsForDays(days) + 1;
        var data = log.Read(from, to);
        if (data.Count == 0) return new LogView { Status = "No temperatures recorded yet" };

        // Order: CPU, GPU, memory, then drives in the order they were first seen.
        var ordered = data.OrderBy(d => d.Series.Kind switch { LogSeriesKind.Cpu => 0, LogSeriesKind.Gpu => 1, LogSeriesKind.Ram => 2, _ => 3 })
                          .ThenBy(d => d.Series.Slot).ToList();
        long first = ordered.Min(d => d.Points[0].Minute);
        var view = new LogView
        {
            // "Recording since…" while the log is younger than the range shown.
            Status = first > from + 60
                ? $"Recording since {When(DateTime.UnixEpoch.AddMinutes(first).ToLocalTime())}"
                : $"Showing the last {LastText(days)}",
            DayColumns = new[] { "CPU", "GPU", "Memory", "Drives" },
        };

        foreach (var d in ordered)
        {
            var lim = Limits(d.Series.Kind, s);
            var st = LogAnalysis.Summarize(d.Points, lim.Warn, lim.Critical)!;
            view.Cards.Add(new LogCardVM
            {
                Title = d.Series.Kind == LogSeriesKind.Ram ? "Memory" : d.Series.Label,
                Subtitle = KindText(d.Series.Kind),
                Average = T(st.Avg),
                Peak = T(st.Peak),
                PeakWhen = When(st.PeakTime),
                Low = T(st.Min),
                AboveWarning = LogAnalysis.Duration(st.MinutesWarm + st.MinutesHot),
                AboveCritical = LogAnalysis.Duration(st.MinutesHot),
                HasHotTime = st.MinutesHot > 0,
                Bins = LogAnalysis.Downsample(d.Points, from, to, bins),
                FromMinute = from, ToMinute = to,
                Warn = lim.Warn, Critical = lim.Critical,
            });
        }

        // Daily summary: CPU, GPU, memory, hottest drive. "avg · peak" per day.
        LogSeriesData? Of(LogSeriesKind k) => ordered.FirstOrDefault(d => d.Series.Kind == k);
        var dailyBy = new List<(Dictionary<DateTime, LogAnalysis.Day> days, Thresholds lim)>();
        foreach (var k in new[] { LogSeriesKind.Cpu, LogSeriesKind.Gpu, LogSeriesKind.Ram })
            dailyBy.Add((Of(k) is { } sd ? LogAnalysis.Daily(sd.Points).ToDictionary(x => x.Date) : new(), Limits(k, s)));
        // Drives: per day, the drive that ran hottest.
        var driveDays = new Dictionary<DateTime, LogAnalysis.Day>();
        var driveLimit = s.SsdThresholds;
        foreach (var d in ordered.Where(d => d.Series.Kind is LogSeriesKind.Ssd or LogSeriesKind.Hdd))
            foreach (var day in LogAnalysis.Daily(d.Points))
                if (!driveDays.TryGetValue(day.Date, out var cur) || day.Max > cur.Max) driveDays[day.Date] = day;
        dailyBy.Add((driveDays, driveLimit));

        var allDates = dailyBy.SelectMany(x => x.days.Keys).Distinct().OrderByDescending(x => x).ToList();
        foreach (var date in allDates)
        {
            var cells = new string[dailyBy.Count];
            var hot = new bool[dailyBy.Count];
            for (int i = 0; i < dailyBy.Count; i++)
            {
                if (!dailyBy[i].days.TryGetValue(date, out var day)) { cells[i] = "—"; continue; }
                cells[i] = $"{T(day.Avg)} · {T(day.Max)}";
                hot[i] = day.Max >= dailyBy[i].lim.Warn;
            }
            var today = DateTime.Now.Date;
            view.Days.Add(new LogDayVM
            {
                Day = date == today ? "Today" : date == today.AddDays(-1) ? "Yesterday" : date.ToString("dddd d MMM", CultureInfo.CurrentCulture),
                Cells = cells,
                Hot = hot,
            });
        }
        return view;
    }
}
