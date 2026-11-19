using System;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Ember.Services.Linux;

namespace Ember;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool Has(string a) => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));

        if (Has("--help") || Has("-h"))
        {
            Console.WriteLine("""
                Ember – PC temperature monitor and overlay

                  ember-monitor                   open Ember (or bring the running window forward)
                  ember-monitor --tray            start in the background (used for start-on-login)
                  ember-monitor --toggle-overlay  show / hide the overlay (bind this to a keyboard shortcut)
                  ember-monitor --exit            quit the running Ember
                  ember-monitor --snapshot DIR    write a sensor report and screenshots to DIR, then exit
                """);
            return 0;
        }

        // Talk to the running instance if there is one.
        bool diagnostics = Has("--snapshot");
        if (!diagnostics)
        {
            string cmd = Has("--toggle-overlay") ? "toggle" : Has("--exit") ? "exit" : Has("--tray") ? "noop" : "show";
            if (SingleInstance.TrySend(cmd)) return 0;
            if (cmd == "exit") return 0; // nothing to quit
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new FontManagerOptions { DefaultFamilyName = "fonts:Inter#Inter" })
            // WM_CLASS must match StartupWMClass in the .desktop file so docks show the right icon.
            .With(new X11PlatformOptions { WmClass = "ember-monitor" })
            .LogToTrace();
}
