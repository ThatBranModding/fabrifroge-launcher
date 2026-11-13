using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;

namespace FabriFrogeLauncher;

public static class GameEngine
{
    private static (string javaExe, string javawExe) ResolveJava(string installRoot)
    {
        var executable = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var guiExecutable = OperatingSystem.IsWindows() ? "javaw.exe" : executable;
        var candidates = new[]
        {
            Path.Combine(installRoot, "runtimes", "temurin-21", "bin"),
            Path.Combine(installRoot, "runtimes", "temurin21", "bin"),
            Path.Combine(installRoot, "runtimes", "java21", "bin"),

            Path.Combine(installRoot, "FabriFroge", "runtimes", "temurin-21", "bin"),
            Path.Combine(installRoot, "FabriFroge", "runtimes", "temurin21", "bin"),
            Path.Combine(installRoot, "FabriFroge", "runtimes", "java21", "bin"),
        };

        foreach (var dir in candidates)
        {
            var java = Path.Combine(dir, executable);
            var javaw = Path.Combine(dir, guiExecutable);
            if (File.Exists(java))
                return (java, javaw);
        }

        var bundled = JavaRuntime.JavaPath(installRoot);
        if (File.Exists(bundled)) return (bundled, bundled);

        throw new Exception(
            $"Java runtime missing. Could not locate {executable} under:\n" +
            string.Join("\n", candidates));
    }

    private static int ClampRamMb(int requestedMb)
    {
        var selected = Math.Clamp(requestedMb, 2048, 65536);
        var availableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableBytes <= 0) return selected;
        var safeLimitMb = (int)Math.Clamp(availableBytes / (1024L * 1024L) - 2048, 2048, 65536);
        return Math.Min(selected, safeLimitMb);
    }

    /// <summary>
    /// Builds a safe, modern set of JVM args for Java 21 + modded Minecraft.
    /// Conservative defaults (no flags that commonly break systems).
    /// </summary>
    private static List<string> BuildDefaultJvmArgs(int ramMb)
    {
        int xmx = Math.Clamp(ramMb, 2048, 65536);
        int xms = Math.Clamp(xmx / 2, 1024, xmx);

        return new List<string>
        {
            $"-Xms{xms}M",
            $"-Xmx{xmx}M",

            "-XX:+UseG1GC",
            "-XX:+ParallelRefProcEnabled",
            "-XX:MaxGCPauseMillis=200",
            "-XX:+UnlockExperimentalVMOptions",
            "-XX:+DisableExplicitGC",

            "-XX:G1NewSizePercent=20",
            "-XX:G1MaxNewSizePercent=60",
            "-XX:G1HeapRegionSize=16M",
            "-XX:G1ReservePercent=20",
            "-XX:InitiatingHeapOccupancyPercent=15",

            "-Dsun.rmi.dgc.server.gcInterval=2147483646",
            "-Dsun.rmi.dgc.client.gcInterval=2147483646",
            "-Dfile.encoding=UTF-8"
        };
    }

    /// <summary>
    /// Inject JVM args into the built java command line.
    /// We insert BEFORE "-cp" so the JVM consumes them (not Minecraft).
    /// </summary>
    private static void InjectJvmArgs(Process proc, IEnumerable<string> jvmArgs, Action<string> log)
    {
        if (proc?.StartInfo == null)
            return;

        var argsToAdd = string.Join(" ", jvmArgs.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        if (string.IsNullOrWhiteSpace(argsToAdd))
            return;

        var original = proc.StartInfo.Arguments ?? "";
        // CmlLib may already emit memory flags; keep exactly one source of truth.
        original = Regex.Replace(original, @"(?i)(?<!\S)-Xm[sx]\S+", "");

        int cpIndex = IndexOfToken(original, " -cp ");
        if (cpIndex < 0)
            cpIndex = IndexOfToken(original, " -classpath ");

        string updated;
        if (cpIndex >= 0)
        {
            updated = original.Insert(cpIndex, " " + argsToAdd);
        }
        else
        {
            updated = argsToAdd + " " + original;
        }

        proc.StartInfo.Arguments = updated;
        log("Injected JVM args into java command line (before -cp).");
    }

    private static int IndexOfToken(string haystack, string token)
    {
        return haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase);
    }

    private static void ConfigureConsole(Process proc, Action<string> log)
    {
        if (proc?.StartInfo == null)
            return;

        proc.StartInfo.UseShellExecute = false;
        proc.StartInfo.CreateNoWindow = true;
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;

        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                log(e.Data);
        };

        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                log("[ERR] " + e.Data);
        };

        proc.EnableRaisingEvents = true;
    }

    private static Task WaitForExitAsync(Process proc, CancellationToken ct)
    {
        if (proc.HasExited)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnExit(object? sender, EventArgs e)
        {
            proc.Exited -= OnExit;
            tcs.TrySetResult(null);
        }

        proc.Exited += OnExit;

        using var registration = ct.Register(() =>
        {
            proc.Exited -= OnExit;
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            tcs.TrySetCanceled(ct);
        });

        if (proc.HasExited)
        {
            proc.Exited -= OnExit;
            return Task.CompletedTask;
        }

        return tcs.Task;
    }

    public static async Task LaunchAsync(
        HttpClient http,
        string installRoot,
        PackManifest manifest,
        MSession? sessionOrNull,
        string offlineUsernameFallback,
        int ramMb,
        Action<string> log,
        CancellationToken ct,
        string? gameDirectoryOverride = null,
        string? javaPathOverride = null)
    {
        var instanceRoot = Installer.GetInstanceRoot(installRoot, manifest.PackId);
        var mcDir = string.IsNullOrWhiteSpace(gameDirectoryOverride)
            ? Path.Combine(instanceRoot, ".minecraft")
            : Path.GetFullPath(gameDirectoryOverride);
        Directory.CreateDirectory(mcDir);

        var (javaExe, javawExe) = ResolveJava(installRoot);
        if (!string.IsNullOrWhiteSpace(javaPathOverride) && File.Exists(javaPathOverride)) javaExe = javaPathOverride;

        // Use java.exe for redirected output capture.
        log($"Using Java for launch: {javaExe}");
        log($"javaw.exe also found at: {javawExe}");

        // Ensure NeoForge installed into THIS instance�s .minecraft
        if (manifest.Loader?.Type?.Equals("neoforge", StringComparison.OrdinalIgnoreCase) == true)
        {
            await NeoForgeClientInstaller.EnsureInstalledAsync(
                http,
                mcDir,
                javaExe,
                manifest.Loader.Version,
                log,
                ct);
        }

        var path = new MinecraftPath(mcDir);
        var launcher = new MinecraftLauncher(path);

        var versionId = manifest.Loader?.Type?.Equals("neoforge", StringComparison.OrdinalIgnoreCase) == true
            ? NeoForgeClientInstaller.GetInstalledVersionId(manifest.Loader.Version)
            : manifest.Minecraft;

        log($"Preparing launch version: {versionId}");

        // Ensure vanilla assets/libs exist
        await launcher.InstallAsync(manifest.Minecraft);

        var session = sessionOrNull ?? MSession.CreateOfflineSession(offlineUsernameFallback);

        var effectiveRamMb = ClampRamMb(ramMb);
        if (effectiveRamMb != ramMb)
            log($"RAM selection capped to {effectiveRamMb} MB based on available system memory.");
        var jvmArgs = BuildDefaultJvmArgs(effectiveRamMb);

        log($"RAM selected: {effectiveRamMb} MB");
        log("JVM Args:");
        foreach (var a in jvmArgs)
            log($"  {a}");

        var opt = new MLaunchOption
        {
            Session = session,
            MaximumRamMb = effectiveRamMb,
            JavaPath = javaExe
        };

        // IMPORTANT: build the actual loader version, not plain vanilla
        var proc = await launcher.BuildProcessAsync(versionId, opt);

        InjectJvmArgs(proc, jvmArgs, log);
        ConfigureConsole(proc, log);

        log("Starting Minecraft...");
        proc.Start();

        try
        {
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            log("Attached launcher console to Minecraft stdout/stderr.");
        }
        catch (Exception ex)
        {
            log($"Failed to begin output capture: {ex}");
        }

        await WaitForExitAsync(proc, ct);
        log("Minecraft process has closed.");
    }
}
