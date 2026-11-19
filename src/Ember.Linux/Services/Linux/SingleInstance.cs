using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ember.Services.Linux;

/// <summary>
/// One Ember per user. The first instance listens on a Unix socket; later launches (app menu,
/// keyboard shortcut running `ember-monitor --toggle-overlay`, autostart) just send it a command.
/// </summary>
public static class SingleInstance
{
    [DllImport("libc")] private static extern uint getuid();

    public static string SocketPath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(runtime) && Directory.Exists(runtime)) return Path.Combine(runtime, "ember-monitor.sock");
            var uid = OperatingSystem.IsLinux() ? getuid().ToString() : Environment.UserName;
            return Path.Combine(Path.GetTempPath(), $"ember-monitor-{uid}.sock");
        }
    }

    private static Socket? _server;

    /// <summary>Sends a command to the running instance. False if none is running.</summary>
    public static bool TrySend(string command)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(SocketPath));
            s.Send(Encoding.UTF8.GetBytes(command + "\n"));
            s.Shutdown(SocketShutdown.Both);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Become the primary instance. Commands are delivered on a background thread.</summary>
    public static bool Listen(Action<string> onCommand)
    {
        try
        {
            var path = SocketPath;
            if (File.Exists(path)) File.Delete(path); // stale socket from a crashed run (TrySend already failed)
            _server = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _server.Bind(new UnixDomainSocketEndPoint(path));
            _server.Listen(4);
            new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        using var c = _server.Accept();
                        var buf = new byte[256];
                        int n = c.Receive(buf);
                        var cmd = Encoding.UTF8.GetString(buf, 0, Math.Max(0, n)).Trim();
                        if (cmd.Length > 0) onCommand(cmd);
                    }
                    catch (ObjectDisposedException) { return; }
                    catch { }
                }
            }) { IsBackground = true, Name = "Ember.Instance" }.Start();
            return true;
        }
        catch { return false; }
    }

    public static void Stop()
    {
        try { _server?.Close(); } catch { }
        try { File.Delete(SocketPath); } catch { }
    }
}
