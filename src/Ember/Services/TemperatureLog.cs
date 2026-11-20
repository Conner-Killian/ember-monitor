using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ember.Services;

public enum LogSeriesKind : byte { Cpu, Gpu, Ram, Ssd, Hdd }

/// <summary>One recorded component (a column of the log).</summary>
public sealed record LogSeries(int Slot, string Key, string Label, LogSeriesKind Kind);

/// <summary>One minute of one component: the lowest, average and highest temperature (°C).</summary>
public readonly record struct LogPoint(long Minute, double Min, double Avg, double Max)
{
    public DateTime LocalTime => DateTime.UnixEpoch.AddMinutes(Minute).ToLocalTime();
}

public sealed class LogSeriesData
{
    public required LogSeries Series { get; init; }
    public required LogPoint[] Points { get; init; } // ascending by minute, gaps where nothing was recorded
}

/// <summary>
/// Temperature log built to be gentle on SSDs.
/// <list type="bullet">
/// <item>One file of fixed size, created once at its full length and used as a ring buffer: minute N
/// goes to row N mod capacity, so old data is overwritten in place and the file never grows.</item>
/// <item>Readings are reduced in memory to one row per minute (min / average / max per component,
/// one byte each at 0.5 °C resolution), and rows are written in a batch every 10 minutes (and when
/// Ember exits): about 0.5 KB per write, no temp files, no rewriting of the file.</item>
/// <item>Each row carries its own minute stamp, so gaps (PC off, Ember closed) and leftovers from a
/// previous lap of the ring are recognised without any index that would need rewriting.</item>
/// </list>
/// Layout: 4 KB header (magic, capacity, component table), then capacity × 52-byte rows.
/// </summary>
public sealed class TemperatureLog : IDisposable
{
    public const int MaxSeries = 16;
    public const int RowBytes = 4 + MaxSeries * 3;
    public const int HeaderBytes = 4096;
    private const int SeriesEntryBytes = 128, SeriesTableOffset = 64, Version = 1;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EMBERLOG");

    public static string DefaultPath => System.IO.Path.Combine(Models.AppSettings.Folder, "temperature-log.bin");
    public static int RowsForDays(int days) => days * 24 * 60;
    public static long FileBytesForDays(int days) => HeaderBytes + (long)RowsForDays(days) * RowBytes;
    public static long Minute(DateTime utc) => (long)Math.Floor((utc - DateTime.UnixEpoch).TotalMinutes);

    private readonly object _gate = new();
    private readonly string _path;
    private FileStream _fs;
    private readonly List<LogSeries> _series = new();
    private readonly Dictionary<string, int> _slotByKey = new();

    // Current minute's accumulators (per slot).
    private long _curMinute = -1;
    private readonly double[] _min = new double[MaxSeries], _max = new double[MaxSeries], _sum = new double[MaxSeries];
    private readonly int[] _count = new int[MaxSeries];
    // Finished minutes not yet written.
    private readonly List<(long minute, byte[] row)> _pending = new();
    private bool _headerDirty;

    public int Capacity { get; private set; }
    public string Path => _path;
    public long FileBytes => HeaderBytes + (long)Capacity * RowBytes;
    /// <summary>Bytes handed to the file since opening (diagnostics: shows how little is written).</summary>
    public long BytesWritten { get; private set; }
    public int Writes { get; private set; }

    private TemperatureLog(string path, FileStream fs, int capacity)
    {
        _path = path; _fs = fs; Capacity = capacity;
    }

    /// <summary>Opens (or creates) the log. A different capacity resizes it, keeping the newest data.</summary>
    public static TemperatureLog Open(string path, int capacityRows)
    {
        if (capacityRows < 60) throw new ArgumentOutOfRangeException(nameof(capacityRows));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var log = TryLoad(path, fs);
            if (log != null)
            {
                if (log.Capacity == capacityRows) return log;
                log.Resize(capacityRows);
                return log;
            }
            // Unreadable (corrupt, or another format): keep a copy and start over.
            fs.Dispose();
            try { File.Move(path, path + ".corrupt", true); } catch { File.Delete(path); }
        }
        var created = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        var fresh = new TemperatureLog(path, created, capacityRows);
        created.SetLength(fresh.FileBytes); // allocate once: the file never changes size afterwards
        fresh.WriteHeader();
        return fresh;
    }

    private static TemperatureLog? TryLoad(string path, FileStream fs)
    {
        try
        {
            var h = new byte[HeaderBytes];
            if (fs.Length < HeaderBytes || fs.Read(h, 0, HeaderBytes) != HeaderBytes) return null;
            if (!h.AsSpan(0, 8).SequenceEqual(Magic) || BitConverter.ToInt32(h, 8) != Version) return null;
            int cap = BitConverter.ToInt32(h, 12), count = BitConverter.ToInt32(h, 20);
            if (cap < 60 || count < 0 || count > MaxSeries || fs.Length != HeaderBytes + (long)cap * RowBytes) return null;
            var log = new TemperatureLog(path, fs, cap);
            for (int i = 0; i < count; i++)
            {
                int o = SeriesTableOffset + i * SeriesEntryBytes;
                var kind = (LogSeriesKind)h[o];
                string key = ReadStr(h, o + 1, 63), label = ReadStr(h, o + 64, 64);
                log._series.Add(new LogSeries(i, key, label, kind));
                log._slotByKey[key] = i;
            }
            return log;
        }
        catch { return null; }
    }

    private static string ReadStr(byte[] b, int offset, int max)
    {
        int len = Array.IndexOf(b, (byte)0, offset, max);
        return Encoding.UTF8.GetString(b, offset, (len < 0 ? offset + max : len) - offset);
    }

    private static void WriteStr(byte[] b, int offset, int max, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        int n = Math.Min(bytes.Length, max - 1);
        while (n > 0 && n < bytes.Length && (bytes[n] & 0xC0) == 0x80) n--; // don't cut a UTF-8 character in half
        Array.Copy(bytes, 0, b, offset, n);
    }

    private void WriteHeader()
    {
        var h = new byte[SeriesTableOffset + MaxSeries * SeriesEntryBytes];
        Magic.CopyTo(h, 0);
        BitConverter.GetBytes(Version).CopyTo(h, 8);
        BitConverter.GetBytes(Capacity).CopyTo(h, 12);
        BitConverter.GetBytes(60).CopyTo(h, 16);
        BitConverter.GetBytes(_series.Count).CopyTo(h, 20);
        foreach (var s in _series)
        {
            int o = SeriesTableOffset + s.Slot * SeriesEntryBytes;
            h[o] = (byte)s.Kind;
            WriteStr(h, o + 1, 63, s.Key);
            WriteStr(h, o + 64, 64, s.Label);
        }
        _fs.Position = 0;
        _fs.Write(h);
        BytesWritten += h.Length;
        _headerDirty = false;
    }

    // ---------------------------------------------------------------- recording

    public IReadOnlyList<LogSeries> Series { get { lock (_gate) return _series.ToArray(); } }

    /// <summary>Adds one sensor reading set. Call for every snapshot; it only touches memory.</summary>
    public void Add(SensorSnapshot snap, DateTime utcNow)
    {
        lock (_gate)
        {
            long minute = Minute(utcNow);
            if (minute != _curMinute) { FinishMinute(); _curMinute = minute; }
            Sample("cpu", snap.Cpu?.Name ?? "Processor", LogSeriesKind.Cpu, snap.CpuTemp);
            Sample("gpu", snap.Gpu?.Name ?? "Graphics", LogSeriesKind.Gpu, snap.GpuTemp);
            Sample("ram", "Memory", LogSeriesKind.Ram, snap.RamTemp);
            foreach (var d in snap.Drives)
                Sample("drive:" + d.Name + "|" + d.Id, d.Name, d.Media == DriveMedia.Hdd ? LogSeriesKind.Hdd : LogSeriesKind.Ssd, d.PrimaryTemp?.Value);
        }
    }

    private void Sample(string key, string label, LogSeriesKind kind, double? value)
    {
        if (value is not double v || !double.IsFinite(v) || v <= 0 || v > 150) return;
        if (!_slotByKey.TryGetValue(key, out int slot))
        {
            if (_series.Count >= MaxSeries) return;
            slot = _series.Count;
            _series.Add(new LogSeries(slot, key, label, kind));
            _slotByKey[key] = slot;
            _headerDirty = true;
        }
        else if (_series[slot].Label != label)
        {
            // e.g. a different primary graphics card: show its current name (one small header write).
            _series[slot] = _series[slot] with { Label = label };
            _headerDirty = true;
        }
        if (_count[slot] == 0) { _min[slot] = _max[slot] = v; _sum[slot] = 0; }
        else { if (v < _min[slot]) _min[slot] = v; if (v > _max[slot]) _max[slot] = v; }
        _sum[slot] += v;
        _count[slot]++;
    }

    private void FinishMinute()
    {
        if (_curMinute < 0) return;
        var row = BuildRow(_curMinute, _min, _sum, _max, _count);
        if (row != null) _pending.Add((_curMinute, row));
        Array.Clear(_count);
        _curMinute = -1;
    }

    private static byte[]? BuildRow(long minute, double[] min, double[] sum, double[] max, int[] count)
    {
        var row = new byte[RowBytes];
        bool any = false;
        BitConverter.GetBytes((uint)minute).CopyTo(row, 0);
        for (int s = 0; s < MaxSeries; s++)
        {
            if (count[s] == 0) continue;
            any = true;
            row[4 + s * 3] = Enc(min[s]);
            row[5 + s * 3] = Enc(sum[s] / count[s]);
            row[6 + s * 3] = Enc(max[s]);
        }
        return any ? row : null;
    }

    private static byte Enc(double c) => (byte)Math.Clamp((int)Math.Round(c * 2), 1, 255); // 0 = no reading
    private static double Dec(byte b) => b / 2.0;

    /// <summary>
    /// Writes finished minutes to disk. <paramref name="includeCurrent"/> also closes the minute in
    /// progress (used on exit). Returns the bytes written.
    /// </summary>
    public int Flush(DateTime utcNow, bool includeCurrent = false)
    {
        lock (_gate)
        {
            if (includeCurrent || (_curMinute >= 0 && Minute(utcNow) > _curMinute)) FinishMinute();
            if (_pending.Count == 0 && !_headerDirty) return 0;
            long before = BytesWritten;
            if (_headerDirty) WriteHeader();
            foreach (var (minute, row) in _pending.OrderBy(p => p.minute))
            {
                _fs.Position = HeaderBytes + (minute % Capacity) * RowBytes;
                _fs.Write(row);
                BytesWritten += row.Length;
            }
            _pending.Clear();
            _fs.Flush(); // hand to the OS; it decides when to write the (few) dirty pages
            Writes++;
            return (int)(BytesWritten - before);
        }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>All recorded minutes in [fromMinute, toMinute], per component, including unsaved ones.</summary>
    public List<LogSeriesData> Read(long fromMinute, long toMinute)
    {
        lock (_gate)
        {
            // Rows from an earlier lap of the ring (older than one capacity before 'to') are stale.
            long oldest = Math.Max(Math.Max(0, fromMinute), toMinute - Capacity + 1);
            if (toMinute < oldest) return new List<LogSeriesData>();

            // Unsaved minutes (pending and in progress) take precedence over what's on disk.
            var memory = new Dictionary<long, byte[]>();
            foreach (var (m, row) in _pending) memory[m] = row;
            if (_curMinute >= 0 && BuildRow(_curMinute, _min, _sum, _max, _count) is { } cur) memory[_curMinute] = cur;

            // Two passes over the rows (count, then fill) so each component gets one array of exactly
            // the right size: a year of minutes would otherwise mean hundreds of MB of growing lists.
            int n = _series.Count;
            var counts = new int[n];
            VisitRows(oldest, toMinute, memory, (_, r) =>
            {
                for (int s = 0; s < n; s++) if (r[5 + s * 3] != 0) counts[s]++;
            });
            var points = counts.Select(c => new LogPoint[c]).ToArray();
            var filled = new int[n];
            VisitRows(oldest, toMinute, memory, (m, r) =>
            {
                for (int s = 0; s < n; s++)
                {
                    byte av = r[5 + s * 3];
                    if (av != 0 && filled[s] < points[s].Length)
                        points[s][filled[s]++] = new LogPoint(m, Dec(r[4 + s * 3]), Dec(av), Dec(r[6 + s * 3]));
                }
            });
            var result = new List<LogSeriesData>();
            for (int s = 0; s < n; s++)
                if (filled[s] > 0) result.Add(new LogSeriesData { Series = _series[s], Points = points[s] });
            return result;
        }
    }

    private delegate void RowVisitor(long minute, ReadOnlySpan<byte> row);

    /// <summary>
    /// Calls <paramref name="visit"/> for every recorded minute in [oldest, to], oldest first (unsaved
    /// rows win over disk). Reads the ring in small chunks; only the requested rows are touched.
    /// Caller holds <see cref="_gate"/>.
    /// </summary>
    private void VisitRows(long oldest, long toMinute, Dictionary<long, byte[]> memory, RowVisitor visit)
    {
        const int ChunkRows = 4096; // ~200 KB per read
        var buf = new byte[(int)Math.Min(ChunkRows, toMinute - oldest + 1) * RowBytes];
        for (long m0 = oldest; m0 <= toMinute; m0 += ChunkRows)
        {
            // One stretch of the ring, or two when it wraps around the end of the file.
            int count = (int)Math.Min(ChunkRows, toMinute - m0 + 1);
            int slot = (int)(m0 % Capacity);
            int part1 = Math.Min(count, Capacity - slot);
            ReadExactly(HeaderBytes + (long)slot * RowBytes, buf, 0, part1 * RowBytes);
            if (part1 < count) ReadExactly(HeaderBytes, buf, part1 * RowBytes, (count - part1) * RowBytes);
            for (int i = 0; i < count; i++)
            {
                long m = m0 + i;
                if (memory.Count > 0 && memory.TryGetValue(m, out var mem)) { visit(m, mem); continue; }
                var r = buf.AsSpan(i * RowBytes, RowBytes);
                if (BitConverter.ToUInt32(r) == (uint)m) visit(m, r); // else empty, or from an earlier lap
            }
        }
    }

    private void ReadExactly(long position, byte[] buffer, int offset, int length)
    {
        _fs.Position = position;
        while (length > 0)
        {
            int n = _fs.Read(buffer, offset, length);
            if (n <= 0) { Array.Clear(buffer, offset, length); return; }
            offset += n; length -= n;
        }
    }

    /// <summary>Every valid row on disk plus unsaved ones (minute → row bytes). Used when resizing.</summary>
    private Dictionary<long, byte[]> LoadRows()
    {
        var rows = new Dictionary<long, byte[]>();
        var all = new byte[(long)Capacity * RowBytes];
        _fs.Position = HeaderBytes;
        int read = 0;
        while (read < all.Length)
        {
            int n = _fs.Read(all, read, all.Length - read);
            if (n <= 0) break;
            read += n;
        }
        for (int i = 0; i < Capacity; i++)
        {
            long m = BitConverter.ToUInt32(all, i * RowBytes);
            if (m != 0 && m % Capacity == i) rows[m] = all.AsSpan(i * RowBytes, RowBytes).ToArray();
        }
        foreach (var (m, row) in _pending) rows[m] = row;
        if (_curMinute >= 0 && BuildRow(_curMinute, _min, _sum, _max, _count) is { } cur) rows[_curMinute] = cur;
        return rows;
    }

    /// <summary>Changes how much the log keeps. Rebuilds the file once, keeping the newest minutes.</summary>
    public void Resize(int capacityRows)
    {
        lock (_gate)
        {
            if (capacityRows == Capacity) return;
            FinishMinute();
            var rows = LoadRows();
            long latest = rows.Count > 0 ? rows.Keys.Max() : 0;
            long keepFrom = latest - Math.Min(Capacity, capacityRows); // exclusive
            var tmp = _path + ".resize";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                var next = new TemperatureLog(tmp, fs, capacityRows);
                next._series.AddRange(_series);
                fs.SetLength(next.FileBytes);
                next.WriteHeader();
                foreach (var (m, row) in rows.Where(r => r.Key > keepFrom).OrderBy(r => r.Key))
                {
                    fs.Position = HeaderBytes + (m % capacityRows) * RowBytes;
                    fs.Write(row);
                }
            }
            _pending.Clear();
            _fs.Dispose();
            File.Move(tmp, _path, true);
            _fs = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            Capacity = capacityRows;
        }
    }

    public void Dispose()
    {
        lock (_gate) _fs.Dispose();
    }
}

/// <summary>Summaries for the Log page.</summary>
public static class LogAnalysis
{
    public sealed record Stats(double Avg, double Min, double Peak, DateTime PeakTime, int Minutes, int MinutesWarm, int MinutesHot);
    public sealed record Day(DateTime Date, double Min, double Avg, double Max, int Minutes);
    /// <summary>One chart column; NaN where nothing was recorded.</summary>
    public readonly record struct Bin(double Min, double Avg, double Max);

    public static Stats? Summarize(IReadOnlyList<LogPoint> pts, double warn, double crit)
    {
        if (pts.Count == 0) return null;
        double sum = 0, min = double.MaxValue, peak = double.MinValue; long peakMinute = 0;
        int warm = 0, hot = 0;
        foreach (var p in pts)
        {
            sum += p.Avg;
            if (p.Min < min) min = p.Min;
            if (p.Max > peak) { peak = p.Max; peakMinute = p.Minute; }
            if (p.Max >= crit) hot++;
            else if (p.Max >= warn) warm++;
        }
        return new Stats(sum / pts.Count, min, peak, DateTime.UnixEpoch.AddMinutes(peakMinute).ToLocalTime(), pts.Count, warm, hot);
    }

    /// <summary>Per local calendar day, newest first.</summary>
    public static List<Day> Daily(IReadOnlyList<LogPoint> pts)
    {
        var days = new Dictionary<DateTime, (double min, double sum, double max, int n)>();
        // Converting every minute to local time is the slow part. Time-zone offsets and DST changes
        // fall on 15-minute boundaries, so the local date is the same throughout a 15-minute block.
        long block = long.MinValue; DateTime date = default;
        foreach (var p in pts)
        {
            long b = Math.DivRem(p.Minute, 15, out long rem) - (rem < 0 ? 1 : 0);
            if (b != block) { block = b; date = p.LocalTime.Date; }
            days[date] = days.TryGetValue(date, out var d)
                ? (Math.Min(d.min, p.Min), d.sum + p.Avg, Math.Max(d.max, p.Max), d.n + 1)
                : (p.Min, p.Avg, p.Max, 1);
        }
        return days.OrderByDescending(kv => kv.Key)
                   .Select(kv => new Day(kv.Key, kv.Value.min, kv.Value.sum / kv.Value.n, kv.Value.max, kv.Value.n))
                   .ToList();
    }

    /// <summary>Reduces minutes to <paramref name="bins"/> columns spanning [from, to].</summary>
    public static Bin[] Downsample(IReadOnlyList<LogPoint> pts, long fromMinute, long toMinute, int bins)
    {
        var res = new Bin[Math.Max(1, bins)];
        var min = new double[res.Length]; var max = new double[res.Length]; var sum = new double[res.Length]; var n = new int[res.Length];
        double span = Math.Max(1, toMinute - fromMinute + 1);
        foreach (var p in pts)
        {
            if (p.Minute < fromMinute || p.Minute > toMinute) continue;
            int i = Math.Min(res.Length - 1, (int)((p.Minute - fromMinute) / span * res.Length));
            if (n[i] == 0) { min[i] = p.Min; max[i] = p.Max; }
            else { min[i] = Math.Min(min[i], p.Min); max[i] = Math.Max(max[i], p.Max); }
            sum[i] += p.Avg; n[i]++;
        }
        for (int i = 0; i < res.Length; i++)
            res[i] = n[i] == 0 ? new Bin(double.NaN, double.NaN, double.NaN) : new Bin(min[i], sum[i] / n[i], max[i]);
        return res;
    }

    public static string Duration(int minutes) =>
        minutes <= 0 ? "Never" : minutes < 60 ? $"{minutes} min" : minutes < 60 * 48 ? $"{minutes / 60.0:0.#} h" : $"{minutes / 1440.0:0.#} days";

    /// <summary>A time-axis mark: a vertical grid line and (room permitting) a label.</summary>
    public readonly record struct Tick(long Minute, string Label);

    /// <summary>
    /// Time-axis marks spaced for the range shown: every 10 minutes for an hour up to every 3 hours for a day, daily for a week,
    /// every 2 days for two weeks, weekly (Mondays) up to ~3 months, monthly beyond.
    /// </summary>
    public static List<Tick> Ticks(long fromMinute, long toMinute)
    {
        var ticks = new List<Tick>();
        var start = DateTime.UnixEpoch.AddMinutes(fromMinute).ToLocalTime();
        var end = DateTime.UnixEpoch.AddMinutes(toMinute).ToLocalTime();
        double minutes = toMinute - fromMinute + 1, days = minutes / 1440.0;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        void Add(DateTime local, string format)
        {
            if (local > start && local <= end)
                ticks.Add(new Tick(TemperatureLog.Minute(local.ToUniversalTime()), local.ToString(format, culture)));
        }
        if (days <= 1.01)
        {
            // Up to a day: 10 min for an hour, 30 min for 3 h, hourly for 6 h, 2 h for 12 h, 3 h for a day.
            int step = minutes <= 61 ? 10 : minutes <= 181 ? 30 : minutes <= 361 ? 60 : minutes <= 721 ? 120 : 180;
            for (var t = start.Date.AddMinutes(Math.Ceiling(start.TimeOfDay.TotalMinutes / step) * step); t <= end; t = t.AddMinutes(step)) Add(t, "t");
        }
        else if (days <= 10)
            for (var t = start.Date.AddDays(1); t <= end; t = t.AddDays(1)) Add(t, "ddd d");
        else if (days <= 21)
            for (var t = start.Date.AddDays(1); t <= end; t = t.AddDays(2)) Add(t, "ddd d");
        else if (days <= 100)
        {
            var t = start.Date.AddDays(1);
            while (t.DayOfWeek != DayOfWeek.Monday) t = t.AddDays(1);
            for (; t <= end; t = t.AddDays(7)) Add(t, "d MMM");
        }
        else
            for (var t = new DateTime(start.Year, start.Month, 1).AddMonths(1); t <= end; t = t.AddMonths(1)) Add(t, t.Month == 1 ? "MMM yyyy" : "MMM");
        return ticks;
    }
}
