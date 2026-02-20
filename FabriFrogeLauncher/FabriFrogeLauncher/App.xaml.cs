using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace FabriFrogeLauncher;

public partial class App : System.Windows.Application
{
    // Where the "real installed" launcher lives (the one your app prefers to run from)
    private static readonly string InstallExePath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FabriFroge", "FabriFrogeLauncher", "FabriFrogeLauncher.exe");

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            // If we are in apply-update mode, we must not do any install-root relaunch logic.
            if (e.Args != null && e.Args.Length > 0 &&
                string.Equals(e.Args[0], "--apply-update", StringComparison.OrdinalIgnoreCase))
            {
                var exitCode = await SelfUpdater.RunApplyUpdateModeAsync(e.Args, LogToConsole);
                LogToConsole($"Apply mode finished. ExitCode={exitCode}");
                Shutdown(exitCode);
                return;
            }

            // DEV escape hatch:
            // - If debugging, let it run from bin\Debug
            // - Or if the env var is set, let it run from wherever we launched it
            var devFlag = Environment.GetEnvironmentVariable("FF_LAUNCHER_DEV");
            var allowLocalRun =
                Debugger.IsAttached ||
                string.Equals(devFlag, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(devFlag, "true", StringComparison.OrdinalIgnoreCase);

            if (!allowLocalRun)
            {
                var currentExe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrWhiteSpace(InstallExePath) &&
                    File.Exists(InstallExePath) &&
                    !PathsEqual(currentExe, InstallExePath))
                {
                    // Relaunch installed exe (so users always run the correct installed copy)
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = InstallExePath,
                        UseShellExecute = true,
                        WorkingDirectory = Path.GetDirectoryName(InstallExePath) ?? Environment.CurrentDirectory
                    });

                    Shutdown(0);
                    return;
                }
            }

            base.OnStartup(e);

            // Normal WPF startup (StartupUri still works)
        }
        catch (Exception ex)
        {
            // Never use WinForms MessageBox here (avoids ambiguity)
            System.Windows.MessageBox.Show(
                ex.ToString(),
                "FabriFroge Launcher - Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            var fa = Path.GetFullPath(a ?? "").TrimEnd(Path.DirectorySeparatorChar);
            var fb = Path.GetFullPath(b ?? "").TrimEnd(Path.DirectorySeparatorChar);
            return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void LogToConsole(string msg)
    {
        try { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}"); } catch { }
    }
}
