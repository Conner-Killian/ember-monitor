using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Ember.Models;

namespace Ember.Services;

/// <summary>
/// Windows in-app update: downloads Ember-Setup-x.y.z.exe and its .sig from the release, checks
/// the signature against the key built into Ember, then runs the installer silently. The installer
/// closes this copy of Ember, replaces the files and starts the new version.
/// </summary>
public static class UpdateInstaller
{
    private const long MaxInstallerBytes = 300L * 1024 * 1024;
    private const string TempPrefix = "Ember-update-";

    // Held until Ember exits: nothing can modify or delete the verified installer while it runs.
    private static FileStream? _lock;

    public static string InstallerName(Version v) => $"Ember-Setup-{UpdateChecker.Format(v)}.exe";

    public static bool HasSignedInstaller(UpdateInfo u) =>
        u.Asset(InstallerName(u.Version)) != null && u.Asset(InstallerName(u.Version) + ".sig") != null;

    /// <summary>Downloads and verifies the installer; returns a read-locked handle to it.</summary>
    public static async Task<FileStream> DownloadVerifiedAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct, bool allowInsecureLoopback = false)
    {
        var name = InstallerName(u.Version);
        var exe = u.Asset(name) ?? throw new InvalidOperationException($"The release has no {name}.");
        var sig = u.Asset(name + ".sig") ?? throw new InvalidOperationException("The release isn't signed, so Ember won't install it automatically.");
        CheckUrl(exe.Url, allowInsecureLoopback);
        CheckUrl(sig.Url, allowInsecureLoopback);
        if (exe.Size > MaxInstallerBytes) throw new InvalidOperationException("The installer is unexpectedly large.");

        var signature = await UpdateChecker.Http.GetStringAsync(sig.Url, ct);
        if (signature.Length > 1024) throw new InvalidOperationException("The signature file is malformed.");

        var dir = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { return await DownloadToAsync(dir, name, exe, signature, progress, ct); }
        catch
        {
            try { Directory.Delete(dir, true); } catch { }
            throw;
        }
    }

    private static async Task<FileStream> DownloadToAsync(string dir, string name, UpdateAsset exe, string signature, IProgress<double>? progress, CancellationToken ct)
    {
        var path = Path.Combine(dir, name);
        using (var res = await UpdateChecker.Http.GetAsync(exe.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            long total = res.Content.Headers.ContentLength ?? exe.Size;
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buf = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                done += n;
                if (done > MaxInstallerBytes) throw new InvalidOperationException("The installer is unexpectedly large.");
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                if (total > 0) progress?.Report(Math.Min(0.99, (double)done / total));
            }
        }

        // Ember runs as administrator and %TEMP% is writable by the user's other programs. Lock the
        // file against writes and deletes, then verify exactly the bytes that will run.
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (!UpdateSignature.Verify(name, UpdateSignature.Hash(locked), signature))
                throw new InvalidOperationException("The download isn't signed with Ember's release key, so it wasn't installed.");
        }
        catch { locked.Dispose(); throw; }
        progress?.Report(1);
        return locked;
    }

    /// <summary>Downloads, verifies and starts the installer. Ember is closed by the installer.</summary>
    public static async Task InstallAsync(UpdateInfo u, bool showWindowAfter, IProgress<double>? progress, CancellationToken ct, bool allowInsecureLoopback = false)
    {
        var locked = await DownloadVerifiedAsync(u, progress, ct, allowInsecureLoopback);
        _lock = locked;
        var log = Path.Combine(AppSettings.Folder, "update-install.log");
        Directory.CreateDirectory(AppSettings.Folder);
        // /MERGETASKS="!pawnio": never start a driver install during an update.
        var args = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=\"!pawnio\" /RELAUNCH={(showWindowAfter ? "show" : "tray")} /LOG=\"{log}\"";
        var psi = new ProcessStartInfo(locked.Name, args) { UseShellExecute = !StartupService.IsElevated() };
        if (psi.UseShellExecute) psi.Verb = "runas"; // only if Ember somehow isn't elevated
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("The installer didn't start.");
        await p.WaitForExitAsync(ct);
        // Still running? Then the installer stopped before replacing anything.
        throw new InvalidOperationException($"The installer stopped early (code {p.ExitCode}). Details: {log}");
    }

    private static void CheckUrl(string url, bool allowInsecureLoopback)
    {
        var uri = new Uri(url);
        if (uri.Scheme == Uri.UriSchemeHttps) return;
        if (allowInsecureLoopback && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return;
        throw new InvalidOperationException("Refusing to download an update over an insecure connection.");
    }

    /// <summary>Removes downloads left from earlier updates.</summary>
    public static void CleanUp()
    {
        try
        {
            foreach (var d in Directory.GetDirectories(Path.GetTempPath(), TempPrefix + "*"))
                try { if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddMinutes(-10)) Directory.Delete(d, true); } catch { }
        }
        catch { }
    }
}
