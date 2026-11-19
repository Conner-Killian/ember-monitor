using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace Ember.Services;

/// <summary>
/// Installs the PawnIO driver (https://pawnio.eu) which LibreHardwareMonitor uses for
/// CPU / RAM / motherboard sensors. Downloads the official signed setup from its GitHub releases.
/// </summary>
public static class DriverService
{
    public const string SetupUrl = "https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe";

    public static async Task InstallInteractive(Window? owner)
    {
        var ok = MessageBox.Show(owner!,
            "Ember will download the official PawnIO driver installer from GitHub (namazso/PawnIO.Setup) and run it.\n\n" +
            "PawnIO is a signed driver that lets monitoring apps read CPU, memory and motherboard sensors. " +
            "Ember restarts afterwards to load it.\n\nContinue?",
            "Install PawnIO driver", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (ok != MessageBoxResult.OK) return;

        var dir = Path.Combine(Path.GetTempPath(), "Ember-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "PawnIO_setup.exe");
        try
        {
            Directory.CreateDirectory(dir);
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Ember-TempMonitor/1.0");
                var bytes = await http.GetByteArrayAsync(SetupUrl);
                await File.WriteAllBytesAsync(path, bytes);
            }

            int exitCode;
            // Ember runs as administrator and the temp folder is writable by any program the user
            // runs. Hold the file open with writes and deletes denied from here until setup has
            // finished, and check its Authenticode signature while it's locked, so what we verified
            // is exactly what runs.
            using (var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!HasValidSignature(path))
                    throw new InvalidOperationException("the downloaded installer isn't validly signed, so it wasn't run");
                using var p = Process.Start(new ProcessStartInfo(path, "-install -silent") { UseShellExecute = false })
                              ?? throw new InvalidOperationException("the installer didn't start");
                await p.WaitForExitAsync();
                exitCode = p.ExitCode;
            }

            if (exitCode is 0 or 3010)
            {
                MessageBox.Show(owner!, "PawnIO is installed. Ember will now restart to load the new sensors.",
                    "Driver installed", MessageBoxButton.OK, MessageBoxImage.Information);
                Restart();
            }
            else
            {
                MessageBox.Show(owner!, $"The PawnIO installer exited with code {exitCode}. You can install it manually from https://pawnio.eu",
                    "Driver install", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner!, "Couldn't install PawnIO: " + ex.Message + "\n\nYou can install it manually from https://pawnio.eu",
                "Driver install", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return;
        Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = true });
        App.Current.ExitApp();
    }

    // ---- Authenticode check (WinVerifyTrust) ----

    internal static bool HasValidSignature(string file)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = file,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,               // WTD_UI_NONE
                fdwRevocationChecks = 0,      // WTD_REVOKE_NONE
                dwUnionChoice = 1,            // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,            // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x10,           // WTD_REVOCATION_CHECK_NONE (no network round-trip)
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);
}
