using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ember.Services;

public sealed record UpdateAsset(string Name, string Url, long Size);

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string Notes, IReadOnlyList<UpdateAsset> Assets)
{
    public UpdateAsset? Asset(string name) => Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public string VersionText => UpdateChecker.Format(Version);
}

/// <summary>Reads the latest release from GitHub (no sign-in needed: the repository is public).</summary>
public static class UpdateChecker
{
    public const string Repo = "Conner-Killian/ember-monitor";
    public const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
    public const string RepoPage = "https://github.com/" + Repo;
    public const string ReleasesPage = RepoPage + "/releases";

    public static readonly Version Current = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    internal static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Ember/{Format(Current)} (+https://github.com/{Repo})");
        return http;
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) t = t[..dash];
        if (!Version.TryParse(t.Contains('.') ? t : t + ".0", out var v)) return false;
        version = Normalize(v);
        return true;
    }

    /// <summary>The latest published release, or null when the repository has none yet.</summary>
    public static async Task<UpdateInfo?> GetLatestAsync(string? feedUrl = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, feedUrl ?? LatestApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (res.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub is limiting requests from this network right now. Try again later.");
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (!TryParseTag(tag, out var version)) throw new FormatException($"Unrecognised release tag \"{tag}\".");
        var assets = new List<UpdateAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var l) ? l : 0;
                if (name != null && url != null) assets.Add(new UpdateAsset(name, url, size));
            }
        string Str(string p) => root.TryGetProperty(p, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
        return new UpdateInfo(version, tag!, Str("html_url") is { Length: > 0 } page ? page : ReleasesPage, Str("body"), assets);
    }
}

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }

/// <summary>
/// Update state for the UI: checks at startup and every few hours (when enabled), and on request.
/// Installing is platform specific (<see cref="Installer"/>); without one, the UI links to the release page.
/// </summary>
public sealed class UpdateManager
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20), Interval = TimeSpan.FromHours(6);
    private readonly Action<Action> _post;
    private Timer? _timer;
    private int _busy;

    public UpdateManager(Action<Action> postToUi) => _post = postToUi;

    public UpdateState State { get; private set; } = UpdateState.Idle;
    public UpdateInfo? Latest { get; private set; }
    public string? Error { get; private set; }
    public double Progress { get; private set; }
    public DateTime? LastChecked { get; private set; }

    /// <summary>Downloads, verifies and runs an update (platform specific). Null = no in-app install.</summary>
    public Func<UpdateInfo, IProgress<double>, CancellationToken, Task>? Installer { get; init; }
    /// <summary>Whether <see cref="Latest"/> has what <see cref="Installer"/> needs (e.g. a signed installer).</summary>
    public Func<UpdateInfo, bool> CanInstallRelease { get; init; } = _ => true;

    public bool IsAvailable => Latest != null && Latest.Version > UpdateChecker.Current;
    public bool CanInstall => IsAvailable && Installer != null && CanInstallRelease(Latest!);
    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Installing;

    /// <summary>UI thread. Raised on every state change.</summary>
    public event Action? Changed;
    /// <summary>UI thread. Raised when a check finds a newer version (each time; callers de-duplicate).</summary>
    public event Action<UpdateInfo>? Found;

    public void SetAutomatic(bool on)
    {
        _timer?.Dispose();
        _timer = on ? new Timer(_ => _post(() => _ = CheckAsync()), null, FirstCheck, Interval) : null;
    }

    public async Task CheckAsync()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            Set(UpdateState.Checking);
            var latest = await UpdateChecker.GetLatestAsync();
            Latest = latest;
            Error = null;
            LastChecked = DateTime.Now;
            Set(IsAvailable ? UpdateState.Available : UpdateState.UpToDate);
            if (IsAvailable) Found?.Invoke(latest!);
        }
        catch (Exception ex)
        {
            Error = ex is TaskCanceledException ? "GitHub didn't respond in time." : ex.Message;
            Set(IsAvailable ? UpdateState.Available : UpdateState.Failed);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    public async Task InstallAsync()
    {
        if (!CanInstall || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            Progress = 0;
            Error = null;
            Set(UpdateState.Downloading);
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                if (p >= 1 && State == UpdateState.Downloading) State = UpdateState.Installing;
                Changed?.Invoke();
            });
            await Installer!(Latest!, progress, CancellationToken.None);
            // Normally the installer closes Ember before we get here; if not, show it as installing.
            Set(UpdateState.Installing);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Set(UpdateState.Failed);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private void Set(UpdateState s) { State = s; Changed?.Invoke(); }

    /// <summary>One line describing the state, for settings pages.</summary>
    public string Describe()
    {
        var cur = UpdateChecker.Format(UpdateChecker.Current);
        return State switch
        {
            UpdateState.Checking => "Checking for updates…",
            UpdateState.Downloading => $"Downloading Ember {Latest?.VersionText}… {Progress * 100:0}%",
            UpdateState.Installing => $"Installing Ember {Latest?.VersionText}. Ember will restart.",
            UpdateState.Available when Error != null => $"Ember {Latest!.VersionText} is available. (Last check failed: {Error})",
            UpdateState.Available => $"Ember {Latest!.VersionText} is available. You have {cur}.",
            UpdateState.UpToDate => $"You're up to date.{Ago()}",
            UpdateState.Failed when IsAvailable => $"The update didn't install: {Error}",
            UpdateState.Failed => $"Couldn't check for updates: {Error}",
            _ => "Checks GitHub for new releases.",
        };
    }

    private string Ago()
    {
        if (LastChecked is not DateTime t) return "";
        var m = (DateTime.Now - t).TotalMinutes;
        return m < 1 ? " Checked just now." : m < 60 ? $" Checked {m:0} min ago." : $" Checked {m / 60:0} h ago.";
    }
}
