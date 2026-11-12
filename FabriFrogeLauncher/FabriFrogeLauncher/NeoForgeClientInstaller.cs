using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FabriFrogeLauncher;

public static class NeoForgeClientInstaller
{
    public static string GetInstallerUrl(string neoforgeVersion)
        => $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{neoforgeVersion}/neoforge-{neoforgeVersion}-installer.jar";

    public static string GetInstalledVersionId(string neoforgeVersion)
        => $"neoforge-{neoforgeVersion}";

    public static bool IsNeoForgeInstalled(string mcDir, string neoforgeVersion)
    {
        var verId = GetInstalledVersionId(neoforgeVersion);
        var verDir = Path.Combine(mcDir, "versions", verId);
        return Directory.Exists(verDir) && File.Exists(Path.Combine(verDir, verId + ".json"));
    }

    public static async Task EnsureInstalledAsync(
        HttpClient http,
        string mcDir,
        string javaExePath,
        string neoforgeVersion,
        Action<string> log,
        CancellationToken ct)
    {
        // Ensure expected base folders exist
        Directory.CreateDirectory(mcDir);
        Directory.CreateDirectory(Path.Combine(mcDir, "versions"));
        Directory.CreateDirectory(Path.Combine(mcDir, "libraries"));

        if (IsNeoForgeInstalled(mcDir, neoforgeVersion))
        {
            log($"NeoForge already installed: {GetInstalledVersionId(neoforgeVersion)}");
            return;
        }

        // Some installers expect this file to exist. An empty JSON is enough.
        var profiles = Path.Combine(mcDir, "launcher_profiles.json");
        if (!File.Exists(profiles))
            await File.WriteAllTextAsync(profiles, "{}", ct);

        var cacheDir = Path.Combine(mcDir, ".fabrifroge-cache");
        Directory.CreateDirectory(cacheDir);

        var installerUrl = GetInstallerUrl(neoforgeVersion);
        var installerJar = Path.Combine(cacheDir, $"neoforge-{neoforgeVersion}-installer.jar");

        log($"Downloading NeoForge installer: {installerUrl}");
        using (var resp = await http.GetAsync(installerUrl, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            await using var fs = new FileStream(installerJar, FileMode.Create, FileAccess.Write, FileShare.None);
            await s.CopyToAsync(fs, ct);
        }

        // Try a few argument styles (NeoForge/Forge installer jars vary by version)
        var argCandidates = new[]
        {
            $"-jar \"{installerJar}\" --installClient \"{mcDir}\"",
            $"-jar \"{installerJar}\" --install-client \"{mcDir}\"",
            $"-jar \"{installerJar}\" --install-client",
            $"-jar \"{installerJar}\" --help"
        };

        Exception? lastErr = null;

        foreach (var args in argCandidates)
        {
            ct.ThrowIfCancellationRequested();

            log($"Running NeoForge installer: {args}");

            try
            {
                var (code, output) = await RunProcessCaptureAsync(
                    fileName: javaExePath,
                    workingDir: mcDir,
                    args: args,
                    ct: ct,
                    onOutput: line => log("[NeoForge] " + line));

                if (code != 0)
                    throw new Exception($"NeoForge installer exited with code {code}. Output: {output}");

                // Did it install?
                if (IsNeoForgeInstalled(mcDir, neoforgeVersion))
                {
                    log($"NeoForge installed: {GetInstalledVersionId(neoforgeVersion)}");
                    return;
                }

                // If not installed, keep trying next candidate
                lastErr = new Exception("Installer completed but expected version json still not found.");
            }
            catch (Exception ex)
            {
                lastErr = ex;
            }
        }

        // If we get here, nothing worked. Dump a quick directory view for debugging.
        var versionsDir = Path.Combine(mcDir, "versions");
        string versionsDump = "(versions folder missing)";
        if (Directory.Exists(versionsDir))
        {
            var dirs = Directory.GetDirectories(versionsDir).Select(Path.GetFileName).OrderBy(x => x).ToArray();
            versionsDump = dirs.Length == 0 ? "(versions folder empty)" : string.Join(", ", dirs);
        }

        throw new Exception(
            "NeoForge installer failed to produce an installed client profile.\n" +
            $"mcDir: {mcDir}\n" +
            $"versions: {versionsDump}\n" +
            $"Last error: {lastErr}");
    }

    private static async Task<(int exitCode, string output)> RunProcessCaptureAsync(
        string fileName,
        string workingDir,
        string args,
        CancellationToken ct,
        Action<string> onOutput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var outputLock = new object();
        var streamsClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedStreams = 0;

        void ReceiveLine(string? line, bool isError)
        {
            if (line == null)
            {
                if (Interlocked.Increment(ref closedStreams) == 2)
                    streamsClosed.TrySetResult();
                return;
            }

            var rendered = isError ? "[stderr] " + line : line;
            lock (outputLock) output.AppendLine(rendered);
            try { onOutput(rendered); } catch { }
        }

        proc.OutputDataReceived += (_, e) => ReceiveLine(e.Data, false);
        proc.ErrorDataReceived += (_, e) => ReceiveLine(e.Data, true);

        if (!proc.Start()) throw new Exception("Failed to start NeoForge installer process.");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var processTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var processToken = CancellationTokenSource.CreateLinkedTokenSource(ct, processTimeout.Token);
        try
        {
            await proc.WaitForExitAsync(processToken.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            if (!ct.IsCancellationRequested)
                throw new TimeoutException("NeoForge installer did not exit within 10 minutes.");
            throw;
        }

        // NeoForge can leave child processes holding inherited stdout/stderr
        // handles after the installer exits. Don't let those pipes keep the UI
        // stuck after the client profile has been installed.
        using var streamDrainTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        streamDrainTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { await streamsClosed.Task.WaitAsync(streamDrainTimeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }

        string captured;
        lock (outputLock) captured = output.ToString();
        return (proc.ExitCode, captured);
    }
}
